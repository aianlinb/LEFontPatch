using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
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
			this.gameDataPath = gameDataPath;
			var result = FindCpp2IlFiles.Find(gameDataPath);
			if (!result.success)
				throw new FileNotFoundException("Unable to find the metadata or assembly in the game data folder: " + gameDataPath);

			manager = new() {
				UseMonoTemplateFieldCache = true,
				UseRefTypeManagerCache = true,
				UseTemplateFieldCache = true,
				MonoTempGenerator = new Cpp2IlTempGenerator(result.metaPath, result.asmPath)
			};
			try {
				using (var tpk = Assembly.GetExecutingAssembly().GetManifestResourceStream(nameof(LEFontPatch) + ".classdata.tpk"))
					manager.LoadClassPackage(tpk); // https://github.com/AssetRipper/Tpk  (Don't use the brotli one)

				sharedassets1 = manager.LoadAssetsFile(Path.Combine(gameDataPath, "sharedassets1.assets"));
				resources = manager.LoadAssetsFile(Path.Combine(gameDataPath, "resources.assets"));

				for (var i = 0; i < sharedassets1.file.Metadata.Externals.Count; ++i)
					if (sharedassets1.file.Metadata.Externals[i].PathName == "resources.assets") {
						dependencyIndex = i;
					}
				if (dependencyIndex < 0)
					throw new InvalidDataException($"Unable to find the dependency index of resources.assets in sharedassets1.assets");
				manager.LoadClassDatabaseFromPackage(resources.file.Metadata.UnityVersion);

				LoadTMPFonts(resources, 469);
				LoadTMPFonts(sharedassets1, 347);
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
			var index = FindScriptIndex(assets, "TMP_FontAsset", exceptedScriptIndex);
			if (index < 0)
				return;

			for (var i = 0; i < assets.file.AssetInfos.Count; ++i) {
				if (assets.file.GetScriptIndex(assets.file.AssetInfos[i]) != index)
					continue;

				var field = manager.GetBaseField(assets, assets.file.AssetInfos[i]);
				if ((AssetRef)field["m_Script"] != (AssetRef)assets.file.Metadata.ScriptTypes[index])
					throw new InvalidDataException("The ScriptIndex of a field doesn't match the one in metadata of assets, the asset file may be broken");

				TMPFonts.Add(assets == resources ? ~i : assets == sharedassets1 ? i :
					throw new ArgumentException("Only resources.assets and sharedassets1.assets are accepted", nameof(assets)), field);
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
			AssetFileInfo info;
			if (index < 0) {
				info = resources.file.Metadata.AssetInfos[~index];
				field = manager.GetBaseField(resources, info);
			} else {
				info = sharedassets1.file.Metadata.AssetInfos[index];
				field = manager.GetBaseField(sharedassets1, info);
			}
			return info;
		}

		public int AddFontFile(byte[] data) {
			var nameLen = MemoryMarshal.Read<int>(data);
			var offset = sizeof(int) + nameLen + sizeof(float); // m_Name, m_LineSpacing
			var mod = offset % 4;
			if (mod != 0)
				offset += 4 - mod;
			data.AsSpan(offset, 12).Clear(); // m_DefaultMaterial
			offset += 12 + sizeof(float); // m_FontSize
			data.AsSpan(offset, 12).Clear(); // m_Texture
			return AddAsset(data, AssetClassID.Font);
		}

		public int AddAtlas(byte[] data) {
			if (!new ReadOnlySpan<byte>(data).EndsWith((ReadOnlySpan<byte>)[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]))
				Throw();
			return AddAsset(data, AssetClassID.Texture2D);

			[DoesNotReturn, DebuggerNonUserCode]
			static void Throw() => throw new ArgumentException("Stream data isn't supported, please move the pixel datas into the Texture2D asset first.", nameof(data));
		}

		public int AddMaterial(byte[] data, int atlas) {
			if (atlas >= 0)
				throw new ArgumentException("Cannot reference assets outside resource.assets", nameof(atlas));

			var result = AddAsset(data, AssetClassID.Material);
			var info = GetAsset(result, out var field);

			var shader = field[1].Children; // m_Shader
			shader.Clear();
			var otherMaterial = resources.file.GetAssetInfo(TMPFonts.First(k => k.Key < 0).Value["material"]["m_PathID"].AsLong);
			shader.AddRange(manager.GetBaseField(resources, otherMaterial)[1].Children); // TextMeshPro/DistanceField

			var texture = field["m_SavedProperties"]["m_TexEnvs"]["Array"].First(f => f[0].AsString == "_MainTex")[1]["m_Texture"];
			new AssetRef(0, GetAsset(atlas).PathId).To(texture); // m_FileID, m_PathID

			info.SetNewData(field.WriteToByteArray());
			return result;
		}

		public void ReplaceTMPFont(int fontIndex, JsonObject data, int atlas, int material, int sourceFontFile = -1) {
			var fromRes = fontIndex < 0;

			AssetRef GetAssetRef(int assetIndex) => new(
					fromRes ? 0 : assetIndex >= 0 ? 0 : dependencyIndex,
					GetAsset(assetIndex).PathId
				);

			if (fromRes && (atlas >= 0 || material >= 0))
				throw new ArgumentException("A TMP_FontAsset in resource.assets cannot reference assets outside resource.assets");

			if ((int)data["m_AtlasPopulationMode"]! == 1) {
				if (fromRes && sourceFontFile >= 0)
					throw new ArgumentException("A TMP_FontAsset in resource.assets cannot reference assets outside resource.assets", nameof(sourceFontFile));
				GetAssetRef(sourceFontFile).To(data["m_SourceFontFile"]!);
			}

			// Two fields that are not in the old version of TextMeshPro which the game uses:
			//data.Remove("m_IsMultiAtlasTexturesEnabled");
			//data.Remove("m_ClearDynamicDataOnBuild");
			// JsonToBytes won't read the fields not in the template, so removing is not necessary

			new AssetRef(
				(fromRes ? TMPFonts.First(k => k.Key < 0) : TMPFonts.First(k => k.Key >= 0)).Value["m_Script"]
			).To(data["m_Script"]!);

			GetAssetRef(material).To(data["material"]!);

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
				var p = Path.Combine(gameDataPath, "~" + assets.name);
				using (var writer = new AssetsFileWriter(p))
					assets.file.Write(writer, 0);
				assets.file.Close();
				File.Move(p, Path.Combine(gameDataPath, assets.name), true);
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