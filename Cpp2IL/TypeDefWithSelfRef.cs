using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;

namespace AssetsTools.NET.Cpp2IL {
	internal struct TypeDefWithSelfRef {
		public Il2CppTypeReflectionData typeRef;
		public Il2CppTypeDefinition typeDef;

		public Dictionary<string, TypeDefWithSelfRef> typeParamToArg = [];
		public string?[] paramNames = [];

		public TypeDefWithSelfRef(Il2CppTypeReflectionData typeRef) {
			this.typeRef = typeRef;
			Il2CppTypeReflectionData? def = typeRef;
			while (def?.isArray ?? false)
				def = def.arrayType;
			typeDef = def?.baseType ?? throw new ArgumentException(null, nameof(typeRef));

			Il2CppTypeReflectionData? tRef = typeRef;
			if (typeRef.isArray)
				tRef = typeRef.arrayType;

			if (tRef?.isGenericType == true
				&& tRef.baseType?.GenericContainer?.GenericParameters.Select(p => p.Name).ToArray() is string?[] names) {
				paramNames = names;
				for (int i = 0; i < tRef.genericParams.Length; i++)
					if (names[i] is string name)
					typeParamToArg.Add(name, new TypeDefWithSelfRef(tRef.genericParams[i]));
			}
		}

		public readonly void AssignTypeParams(TypeDefWithSelfRef parentTypeDef) {
			if (parentTypeDef.typeParamToArg.Count > 0 && typeRef.isGenericType) {
				for (int i = 0; i < typeRef.genericParams.Length; i++) {
					if (paramNames[i] is not string name)
						continue;

					TypeDefWithSelfRef genTypeRef = new(typeRef.genericParams[i]);
					if (!string.IsNullOrEmpty(genTypeRef.typeRef.variableGenericParamName)) {
						if (parentTypeDef.typeParamToArg.TryGetValue(genTypeRef.typeRef.variableGenericParamName, out TypeDefWithSelfRef mappedType))
							typeParamToArg[name] = mappedType;
					} else {
						// nested generic, AssignTypeParams should get it
						genTypeRef.AssignTypeParams(parentTypeDef);
						typeParamToArg[name] = genTypeRef;
					}
				}
			}
		}

		public readonly TypeDefWithSelfRef SolidifyType(TypeDefWithSelfRef typeDef) {
			typeDef.AssignTypeParams(this);
			return typeParamToArg.TryGetValue(typeDef.typeRef.ToString(), out TypeDefWithSelfRef retType) ? retType : typeDef;
		}

		public static implicit operator TypeDefWithSelfRef(Il2CppTypeReflectionData typeReference) {
			return new TypeDefWithSelfRef(typeReference);
		}
	}
}