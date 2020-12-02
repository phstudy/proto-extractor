using Mono.Cecil;
using protoextractor.IR;
using System;
using System.Collections.Generic;
using System.Linq;

namespace protoextractor.decompiler.c_sharp.inspectors
{
	class GoogleFBSInspector
	{
		public static bool MatchDecompilableClasses(TypeDefinition t)
		{
			return (t.IsClass && t.Interfaces.Any(i => i.InterfaceType.Name.Equals("IFlatbufferObject")));
		}

		public static bool MatchStaticConstructor(MethodDefinition m)
		{
			return (m.IsConstructor && m.IsStatic);
		}

		public static bool MatchDeserializeMethod(MethodDefinition method)
		{
			// [Message]::MergeFrom(CodedInputStream)
			return method.Name.Equals("MergeFrom") && method.Parameters.Count == 1 &&
				   method.Parameters[0].ParameterType.Name.Equals("CodedInputStream");
		}

		public static bool MatchSerializeMethod(MethodDefinition method)
		{
			// [Message]::WriteTo(CodedOutputStream)
			return method.Name.Equals("WriteTo") && method.Parameters.Count == 1 &&
				   method.Parameters[0].ParameterType.Name.Equals("CodedOutputStream");
		}

		public static void DeserializeOnCall(CallInfo info, List<byte> writtenBytes,
											 List<IRClassProperty> properties)
		{

		}

		// A method was called by our inspected method. We use the collected information (our environment)
		// to extract information about the type (and fields).
		public static void SerializeOnCall(CallInfo info, List<byte> writtenBytes,
										   List<IRClassProperty> properties)
		{
			if (!info.Method.Name.StartsWith("Write") || info.Method.Name.StartsWith("WriteTo"))
			{
				// We are in no relevant method.
				return;
			}
			if (info.Method.Name.Equals("WriteRawTag"))
			{
				// Used to write tag information.
				return;
			}
			// Name of the type of the proto equivalent of the property.
			var type = info.Method.Name.Substring(5);
			// Name of the property
			var propName = info.Arguments[1].ToString();
			propName = propName.Substring(propName.IndexOf("get_") + 4);
			// Cut of parenthesis.
			propName = propName.Substring(0, propName.Length - 2);

			// Locate property from property list
			var property = properties.First(p => p.Name.Equals(propName));

			// Get more specific type.
			var specificType = InspectorTools.LiteralTypeMapper(type);
			property.Type = specificType;

			// Label and fieldTag are already set!
		}

		// Read out static constructor for FieldCodec method calls.
		public static void StaticCctorOnCall(CallInfo info, List<IRClassProperty> properties,
											 List<ulong> recordedTags)
		{
			// We want ForMessage, ForInt32, ForXXX
			if (!info.Method.Name.StartsWith("For"))
			{
				return;
			}

			// The tag is at the first parameter of the method call.
			// Properly cast it, we want an unsigned long (64 bits).
			ulong tag;
			var obj = info.Arguments.First();
			var objType = Type.GetTypeCode(obj.GetType());
			switch (objType)
			{
				case TypeCode.Int32:
					tag = (ulong)(int)obj;
					break;
				case TypeCode.UInt32:
					tag = (ulong)(uint)obj;
					break;
				case TypeCode.Int64:
					tag = (ulong)(long)obj;
					break;
				case TypeCode.UInt64:
					tag = (ulong)obj;
					break;
				default:
					throw new Exception("Unrecognized tag type!");
			}
			// Store the tag into the list for later use.
			recordedTags.Add(tag);
		}

		// Read out static constructor for setters off _repeated_XXX_codec fields.
		public static void StaticCctorOnStore(StoreInfo info, List<IRClassProperty> properties,
											  List<ulong> recordedTags)
		{
			if (!info.Field.Name.StartsWith("_repeated_"))
			{
				return;
			}

			// Extract the property name of the field we are storing data into.
			var property = info.Field.Name.Substring(10); // Cut off '_repeated_' from front
			property = property.Substring(0, property.Length - 6); // Cut off '_codec' at the end
			// Uppercase first character of property name, because .. inconsistencies..
			property = Char.ToUpper(property[0]) + property.Substring(1);

			// Find property.
			IRClassProperty prop;
			var propEnum = properties.Where(p => p.Name.Equals(property));
			if (propEnum.Any())
			{
				prop = propEnum.First();
			}
			else
			{
				// Inconsistency happened in naming the backing field for repeated properties.
				// Retry with a trimmed property name.
				prop = properties.First(p => p.Name.Trim('_').Equals(property));
			}
			// POP matching tag for this property
			var tag = recordedTags[0];
			recordedTags.RemoveAt(0);

			// Split tag into bytes.
			var bytes = new List<byte>();
			// Bytes must be gotten in Little endian format!
			// Only send in the minimum of bytes.
			for (int i = 0; i < sizeof(ulong); ++i)
			{
				// Shift bits by bytesize (8) and take last 8 bits.
				var b = (byte)((tag >> (8 * i)) & 0xff);
				// Push the result onto the byte list.
				bytes.Add(b);
				// If the MSB of this byte is 0, break the loop.
				// => this guarantees minimal written bytes.
				if (0 == (b & 0x80))
				{
					break;
				}
			}
			// bytes contains the written tag, split per 8 bits, in little endian.

			// Check if the field is packed.
			prop.Options.IsPacked = InspectorTools.TagToPackedSpecifier(bytes, prop.Type);

			return;
		}

		// Get all properties from the type we are analyzing.
		public static List<IRClassProperty> ExtractClassProperties(TypeDefinition _subjectClass,
																   out List<TypeDefinition> references)
		{
			// Contains all references (TypeDefinitions) that are referenced by this class.
			references = new List<TypeDefinition>();

			// All properties for the given class.
			List<IRClassProperty> properties = new List<IRClassProperty>();

			// Propertye != field; see SilentOrbitInspector.ExtractClassproperties(..)
            int cnt = 1;
			foreach (var property in _subjectClass.Properties)
			{
                if (property.Name.Equals("ByteBuffer"))
                {
                    continue;
                }

				FieldLabel label = FieldLabel.OPTIONAL;

                if (property.Name.EndsWith("Length") && !property.Name.Equals("Length"))
                {
                    property.Name = property.Name.Replace("Length", "");
                    MethodDefinition definition = _subjectClass.Methods.First(method => method.Name.Equals(property.Name));
                    if (definition.ReturnType.IsGenericInstance)
                    {
                        property.PropertyType = ((GenericInstanceType) definition.ReturnType).GenericArguments[0];
                    }
                    else
                    {
                        property.PropertyType = definition.ReturnType;
                    }

                    label = FieldLabel.REPEATED;
				}

				// Object which the current property references.
				TypeDefinition refDefinition;
				// Field options (directly related to protobuf schema)
				IRClassProperty.ILPropertyOptions opts = new IRClassProperty.ILPropertyOptions();
				// Add label to the property options.
				opts.Label = label;

				// Fetch the IR type of the property. - Doesn't actually matter, the Serialize handler will overwrite this.
				PropertyTypeKind propType = InspectorTools.DefaultTypeMapper(property, out refDefinition);

				// Construct IR reference placeholder.
				IRTypeNode irReference = null;
				if (propType == PropertyTypeKind.TYPE_REF)
				{
					irReference = InspectorTools.ConstructIRType(refDefinition);
					// And save the reference TYPEDEFINITION for the caller to process.
					references.Add(refDefinition);
				}

				// Fetch the fieldNumber for this property.
				var tag = cnt++;

                if (tag != -1)
                {
                    // Add it to the options.
                    opts.PropertyOrder = tag;

                    // Construct the IR property and store it.
                    var prop = new IRClassProperty()
                    {
                        Name = property.Name,
                        Type = propType,
                        ReferencedType = irReference,
                        Options = opts,
                    };
                    properties.Add(prop);
                }
			}

			return properties;
		}
	}
}
