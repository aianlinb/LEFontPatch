using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AssetsTools.NET;
using AssetsTools.NET.Cpp2IL;
using AssetsTools.NET.Extra;

namespace LEFontPatch {
	public sealed class LEFontManager : IDisposable {
		private readonly string gameDataPath;
		private readonly AssetsManager manager;
		private readonly AssetsFileInstance resources;
		private readonly AssetsFileInstance sharedassets1;
		private bool rModified = false;
		private bool sModified = false;
		private readonly int dependencyIndex = -1;
		/// <summary>
		/// TextMeshPro Fonts
		/// </summary>
		public readonly Dictionary<int, AssetTypeValueField> TMPFonts = [];

		public LEFontManager(string gameDataPath) {
			this.gameDataPath = Path.GetFullPath(gameDataPath);
			var result = FindCpp2IlFiles.Find(gameDataPath);
			if (!result.success)
				throw new FileNotFoundException("Unable to find the metadata or assembly in the game data folder: " + gameDataPath);

			manager = new() {
				UseTemplateFieldCache = true,
				UseMonoTemplateFieldCache = true,
				UseRefTypeManagerCache = true,
				UseQuickLookup = false,
				MonoTempGenerator = new Cpp2IlTempGenerator(result.metaPath, result.asmPath)
			};
			try {
				using (var tpk = Assembly.GetExecutingAssembly().GetManifestResourceStream(nameof(LEFontPatch) + ".classdata.tpk")!)
					manager.LoadClassPackage(tpk); // Type Tree Tpk from: https://github.com/AssetRipper/Tpk (Don't use the brotli one)

				sharedassets1 = manager.LoadAssetsFile(gameDataPath + "/sharedassets1.assets");
				resources = manager.LoadAssetsFile(gameDataPath + "/resources.assets");

				for (var i = 0; i < sharedassets1.file.Metadata.Externals.Count; ++i)
					if (sharedassets1.file.Metadata.Externals[i].PathName == "resources.assets")
						dependencyIndex = i;
				if (dependencyIndex < 0)
					throw new InvalidDataException($"Unable to find the dependency index of resources.assets in sharedassets1.assets");
				manager.LoadClassDatabaseFromPackage(resources.file.Metadata.UnityVersion);

				LoadTMPFonts(resources, 467);
				LoadTMPFonts(sharedassets1, 432);
			} catch {
				Dispose();
				throw;
			}
		}

		public int FindScriptIndex(AssetsFileInstance assets, string typeName, int excepted = 0) {
			var monoScripts = assets.file.Metadata.ScriptTypes;
			if ((uint)excepted >= (uint)monoScripts.Count)
				excepted = 0;
			for (var i = 0; i < excepted; ++i)
				if (Found(i))
					return i;
			for (var i = excepted; i < assets.file.Metadata.ScriptTypes.Count; ++i)
				if (Found(i))
					return i;
			return -1;

			bool Found(int index) => manager.GetExtAsset(assets, monoScripts[index].FileId, monoScripts[index].PathId, false, AssetReadFlags.SkipMonoBehaviourFields)
				.baseField["m_Name"].AsString == typeName;
		}

		private void LoadTMPFonts(AssetsFileInstance assets, int exceptedScriptIndex = 0) {
			if (assets != resources && assets != sharedassets1)
				throw new ArgumentException("Only resources.assets and sharedassets1.assets are allowed", nameof(assets));

			foreach (var (i, field) in EnumerateAssetsOfType(assets, "TMP_FontAsset", exceptedScriptIndex))
				TMPFonts.Add(assets == resources ? ~i : i, field);
		}

		private IEnumerable<(int index, AssetTypeValueField field)> EnumerateAssetsOfType(AssetsFileInstance assets, string typeName, int exceptedScriptIndex = 0) {
			var index = FindScriptIndex(assets, typeName, exceptedScriptIndex);
			if (index < 0)
				yield break;

			for (var i = 0; i < assets.file.AssetInfos.Count; ++i) {
				if (assets.file.GetScriptIndex(assets.file.AssetInfos[i]) != index)
					continue;

				var field = manager.GetBaseField(assets, assets.file.AssetInfos[i]);
				if ((AssetRef)field["m_Script"] != (AssetRef)assets.file.Metadata.ScriptTypes[index])
					throw new InvalidDataException("The ScriptIndex of a field doesn't match the one in metadata of assets, the asset file may be broken");

				yield return (i, field);
			}
		}

		private int AddAsset(byte[] data, AssetClassID classId, ushort scriptIndex = ushort.MaxValue) {
			var asset = AssetFileInfo.Create(resources.file, resources.file.Metadata.AssetInfos[^1].PathId + 1, (int)classId, scriptIndex, manager.ClassDatabase);
			asset.Replacer = new ContentReplacerFromBuffer(data);
			var index = ~resources.file.Metadata.AssetInfos.Count;
			resources.file.Metadata.AssetInfos.Add(asset);
			rModified = true;
			return index;
		}

		public AssetFileInfo GetAsset(int index) => index < 0 ? resources.file.Metadata.AssetInfos[~index]
														: sharedassets1.file.Metadata.AssetInfos[index];
		public AssetFileInfo GetAsset(int index, out AssetTypeValueField field) {
			var info = GetAsset(index);
			if (!TMPFonts.TryGetValue(index, out field!))
				field = manager.GetBaseField(index < 0 ? resources : sharedassets1, info);
			return info;
		}

		public int AddFontFile(byte[] data) {
			var nameLen = MemoryMarshal.Read<int>(data);
			var offset = sizeof(int) + nameLen + sizeof(float); // m_Name, m_LineSpacing
			var mod = offset % 4;
			if (mod != 0)
				offset += 4 - mod;
			const int AssetRefSize = sizeof(int) + sizeof(long); // m_FileID, m_PathID
			data.AsSpan(offset, AssetRefSize).Clear(); // m_DefaultMaterial
			offset += AssetRefSize + sizeof(float); // m_FontSize
			data.AsSpan(offset, AssetRefSize).Clear(); // m_Texture
			return AddAsset(data, AssetClassID.Font);
		}

		public int AddAtlas(byte[] data) {
			[DoesNotReturn, DebuggerNonUserCode]
			static void Throw() => throw new ArgumentException("Stream data isn't supported, please move the pixel datas into the Texture2D asset first.", nameof(data));

			if (!new ReadOnlySpan<byte>(data).EndsWith((ReadOnlySpan<byte>)[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]))
				Throw();
			return AddAsset(data, AssetClassID.Texture2D);
		}

		public int AddMaterial(byte[] data, int atlas) {
			[DoesNotReturn, DebuggerNonUserCode]
			static void Throw() => throw new ArgumentException("Cannot reference assets outside resource.assets", nameof(atlas));
			if (atlas >= 0)
				Throw();

			var result = AddAsset(data, AssetClassID.Material);
			var info = GetAsset(result, out var field);

			var shader = field["m_Shader"].Children;
			var otherMaterial = resources.file.GetAssetInfo(TMPFonts.First(kvp => kvp.Key < 0).Value["material"]["m_PathID"].AsLong);
			shader.Clear();
			shader.AddRange(manager.GetBaseField(resources, otherMaterial)["m_Shader"].Children); // TextMeshPro/DistanceField

			var texture = field["m_SavedProperties"]["m_TexEnvs"]["Array"].First(f => f[0].AsString == "_MainTex")[1]["m_Texture"];
			new AssetRef(0, GetAsset(atlas).PathId).To(texture); // m_FileID, m_PathID

			info.SetNewData(field);
			return result;
		}

		public void ReplaceTMPFont(int fontIndex, JsonObject data, int atlas, int material, int sourceFontFile = -1) {
			var fromRes = fontIndex < 0;

			AssetRef GetAssetRef(int assetIndex) => new(
					fromRes ? 0 : assetIndex >= 0 ? 0 : (dependencyIndex + 1),
					GetAsset(assetIndex).PathId
				);

			if (fromRes && (atlas >= 0 || material >= 0))
				throw new ArgumentException("A TMP_FontAsset in resource.assets cannot reference assets outside resource.assets");

			new AssetRef(
				(fromRes ? TMPFonts.First(k => k.Key < 0) : TMPFonts.First(k => k.Key >= 0)).Value["m_Script"]
			).To(data["m_Script"]!);

			GetAssetRef(material).To(data["material"]!);

			if ((int)data["m_AtlasPopulationMode"]! == 1) {
				if (fromRes && sourceFontFile >= 0)
					throw new ArgumentException("A TMP_FontAsset in resource.assets cannot reference assets outside resource.assets", nameof(sourceFontFile));
				if (GetAsset(sourceFontFile).TypeId != (int)AssetClassID.Font)
					throw new ArgumentException("The provided sourceFontFile is not a Font asset", nameof(sourceFontFile));
				GetAssetRef(sourceFontFile).To(data["m_SourceFontFile"]!);
			} else
				default(AssetRef).To(data["m_SourceFontFile"]!);

			data["m_AtlasTextures"]!["Array"] = new JsonArray((JsonObject)GetAssetRef(atlas));

			var info = GetAsset(fontIndex);
			var bytes = JsonToBytes(data, manager.GetTemplateBaseField(fromRes ? resources : sharedassets1, info));
			info.Replacer = new ContentReplacerFromBuffer(bytes);
			if (fromRes)
				rModified = true;
			else
				sModified = true;
			TMPFonts[fontIndex] = manager.GetBaseField(fromRes ? resources : sharedassets1, info);
		}

		public IEnumerable<int> GetCharacters(int fontIndex, out int count) {
			var list = TMPFonts[fontIndex]["m_CharacterTable"]["Array"].Children;
			count = list.Count;
			return list.Select(c => c["m_Unicode"].AsInt);
		}

		public void RemoveCharacters(IEnumerable<int> fontIndexs, ICollection<int> characters) {
			foreach (var i in fontIndexs) {
				var font = TMPFonts[i];
				var chars = font["m_CharacterTable"]["Array"];
				if (chars.Children.RemoveAll(c => characters.Contains(c["m_Unicode"].AsInt)) > 0) {
					chars.AsArray = new(chars.Children.Count);
					if (i < 0) {
						resources.file.AssetInfos[~i].SetNewData(font);
						rModified = true;
					} else {
						sharedassets1.file.AssetInfos[i].SetNewData(font);
						sModified = true;
					}
				}
			}
		}

		public string DumpFontFallbacks() {
			var sb = new StringBuilder();
			sb.AppendLine("Global Fallbacks:");
			var settings = EnumerateAssetsOfType(resources, "TMP_Settings", 158).FirstOrDefault().field;
			if (settings != null) {
				foreach (var f in settings["m_fallbackFontAssets"]["Array"].Children)
					Dump((AssetRef)f, true);
				Dump((AssetRef)settings["m_defaultFontAsset"], true);
			}
			sb.AppendLine();

			foreach (var (index, field) in TMPFonts) {
				sb.AppendLine(field["m_Name"].AsString + ":");
				foreach (var f in field["m_FallbackFontAssetTable"]["Array"].Children)
					Dump((AssetRef)f, index < 0);
			}

			return sb.ToString();

			void Dump(AssetRef aref, bool fromRes) {
				if (aref.IsNull)
					return;

				Debug.Assert(fromRes && aref.FileID == 0 || !fromRes && (aref.FileID == 0 || aref.FileID == dependencyIndex + 1),
					$"Found a fallback font is neither in resources.assets nor sharedassets1.assets: ({aref.FileID}, {aref.PathID})");
				fromRes = fromRes || aref.FileID != 0;
				var (i, data) = TMPFonts.First(kvp => (fromRes ? kvp.Key < 0 : kvp.Key >= 0) &&
					aref.PathID == GetAsset(kvp.Key).PathId);
				sb.AppendLine("\t| " + data["m_Name"].AsString);
			}
		}

		public void Save() {
			if (rModified) {
				Save(resources);
				rModified = false;
			}
			if (sModified) {
				Save(sharedassets1);
				sModified = false;
			}
			void Save(AssetsFileInstance assets) {
				var p = $"{gameDataPath}/~{assets.name}";
				using (var writer = new AssetsFileWriter(p))
					assets.file.Write(writer, 0);
				assets.file.Close();
				File.Move(p, $"{gameDataPath}/{assets.name}", true);
			}
		}

		public void Dispose() {
			manager.UnloadAll(true);
		}

		private static byte[] JsonToBytes(JsonObject json, AssetTypeTemplateField tempField) {
			var ms = new MemoryStream();
			var writer = new AssetsFileWriter(ms);
			Recurse(tempField, json);
			return ms.ToArray();

			void Recurse(AssetTypeTemplateField tempField, JsonNode node) {
				var align = tempField.IsAligned;

				if (!tempField.HasValue && !tempField.IsArray) {
					foreach (var tf in tempField.Children)
						Recurse(tf, node[tf.Name] ?? throw new JsonException($"Missing field {tf.Name} of {tempField.Name} in JSON."));
					if (align)
						writer.Align();
				} else if (tempField.HasValue && tempField.ValueType == AssetValueType.ManagedReferencesRegistry) {
					throw new NotImplementedException("SerializeReference not supported in JSON import yet!");
				} else {
					switch (tempField.ValueType) {
						case AssetValueType.Bool:
							writer.Write((bool)node);
							break;
						case AssetValueType.UInt8:
							writer.Write((byte)node);
							break;
						case AssetValueType.Int8:
							writer.Write((sbyte)node);
							break;
						case AssetValueType.UInt16:
							writer.Write((ushort)node);
							break;
						case AssetValueType.Int16:
							writer.Write((short)node);
							break;
						case AssetValueType.UInt32:
							writer.Write((uint)node);
							break;
						case AssetValueType.Int32:
							writer.Write((int)node);
							break;
						case AssetValueType.UInt64:
							writer.Write((ulong)node);
							break;
						case AssetValueType.Int64:
							writer.Write((long)node);
							break;
						case AssetValueType.Float:
							writer.Write((float)node);
							break;
						case AssetValueType.Double:
							writer.Write((double)node);
							break;
						case AssetValueType.String:
							align = true;
							writer.WriteCountStringInt32((string?)node ?? "");
							break;
						case AssetValueType.ByteArray:
							var array = node.AsArray();
							writer.Write(array.Count);
							writer.Write(array.Select(i => (byte)(int)i!).ToArray());
							break;
					}

					// have to do this because of bug in MonoDeserializer
					if (tempField.IsArray && tempField.ValueType != AssetValueType.ByteArray) {
						// children[0] is size field, children[1] is the data field
						var tf = tempField.Children[1];
						var array = node!.AsArray();
						writer.Write(array.Count);
						foreach (var token in array)
							Recurse(tf, token!);
					}

					if (align)
						writer.Align();
				}
			}
		}

		private readonly struct AssetRef(int fileID, long pathID) : IEquatable<AssetRef>, IComparable<AssetRef>, ICloneable {
			public readonly int FileID = fileID;
			public readonly long PathID = pathID;
			public bool IsNull => FileID == 0 && PathID == 0;

			public AssetRef(AssetPPtr aptr) : this(aptr.FileId, aptr.PathId) { }
			public AssetRef(AssetTypeValueField field) : this(field[0].AsInt, field[1].AsLong) { }
			public AssetRef(JsonNode node) : this((int)node["m_FileID"]!, (long)node["m_PathID"]!) { }

			public static explicit operator AssetRef(AssetPPtr aptr) => new(aptr);
			public static explicit operator AssetRef(AssetTypeValueField field) => new(field);
			public static explicit operator AssetRef(JsonNode node) => new(node);
			public static explicit operator AssetPPtr(AssetRef aref) => new(aref.FileID, aref.PathID);
			public static explicit operator JsonObject(AssetRef aref) => new([
				new ("m_FileID", aref.FileID),
				new ("m_PathID", aref.PathID)
			]);
			public void To(AssetTypeValueField field) {
				field["m_FileID"].AsInt = FileID;
				field["m_PathID"].AsLong = PathID;
			}
			public void To(JsonNode node) {
				node["m_FileID"] = FileID;
				node["m_PathID"] = PathID;
			}
			public bool Equals(AssetRef other) => FileID == other.FileID && PathID == other.PathID;
			public override bool Equals(object? obj) => obj is AssetRef other && Equals(other);
			public override int GetHashCode() => FileID ^ PathID.GetHashCode();
			public int CompareTo(AssetRef other) {
				if (FileID < other.FileID)
					return -1;
				if (FileID > other.FileID)
					return 1;
				return PathID.CompareTo(other.PathID);
			}
			public static bool operator ==(AssetRef left, AssetRef right) => left.Equals(right);
			public static bool operator !=(AssetRef left, AssetRef right) => !left.Equals(right);
			public static bool operator <(AssetRef left, AssetRef right) => left.CompareTo(right) < 0;
			public static bool operator <=(AssetRef left, AssetRef right) => left.CompareTo(right) <= 0;
			public static bool operator >(AssetRef left, AssetRef right) => left.CompareTo(right) > 0;
			public static bool operator >=(AssetRef left, AssetRef right) => left.CompareTo(right) >= 0;
			public object Clone() => this;
		}
	}
}