//
// Copyright (c) 2012-2024 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in the LICENSE file.

using System;
using System.Collections.Generic;
using System.IO;
using Antmicro.Migrant.Customization;
using Antmicro.Migrant.Utilities;
using Antmicro.Migrant.BultinSurrogates;
using Antmicro.Migrant.VersionTolerance;
using System.Collections.ObjectModel;
using Antmicro.Migrant.Generators;
using System.Collections;
using System.Text;
using System.Linq;

namespace Antmicro.Migrant
{
    /// <summary>
    /// Provides the mechanism for binary serialization and deserialization of objects.
    /// </summary>
    /// <remarks>
    /// Please consult the general serializer documentation to find the limitations
    /// and constraints which serialized objects must fullfill.
    /// </remarks>
    public class Serializer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Antmicro.Migrant.Serializer"/> class.
        /// </summary>
        /// <param name='settings'>
        /// Serializer's settings, can be null or not given, in that case default settings are
        /// used.
        /// </param>
        public Serializer(Settings settings = null)
        {
            if(settings == null)
            {
                settings = new Settings(); // default settings
            }
            this.settings = settings;

            objectsForSurrogates = new SwapList();
            surrogatesForObjects = new SwapList();

            if(settings.SupportForISerializable)
            {
                ForObject<System.Runtime.Serialization.ISerializable>().SetSurrogate(x => new SurrogateForISerializable(x));
                ForSurrogate<SurrogateForISerializable>().SetObject(x => x.Restore());
                ForObject<Delegate>().SetSurrogate<Func<Delegate, object>>(null); //because Delegate implements ISerializable but we support it directly.
            }

            if(settings.SupportForIXmlSerializable)
            {
                ForObject<System.Xml.Serialization.IXmlSerializable>().SetSurrogate(x => new SurrogateForIXmlSerializable(x));
                ForSurrogate<SurrogateForIXmlSerializable>().SetObject(x => x.Restore());
            }

            ForObject(typeof(Hashtable)).SetSurrogate(x => new SurrogateForHashtable((Hashtable)x));
            ForSurrogate<SurrogateForHashtable>().SetObject(x => x.Restore());

            ForObject(typeof(ReadOnlyCollection<>)).SetSurrogateGenericType(typeof(SurrogateForReadOnlyCollection<>));
            ForSurrogate(typeof(SurrogateForReadOnlyCollection<>)).SetObject(x => ((ISurrogateRestorer)x).Restore());

            ForObject(typeof(Dictionary<,>)).SetSurrogateGenericType(typeof(SurrogateForDictionary<,>));
            ForSurrogate(typeof(SurrogateForDictionary<,>)).SetObject(x => ((ISurrogateRestorer)x).Restore());

            ForObject(typeof(HashSet<>)).SetSurrogateGenericType(typeof(SurrogateForHashSet<>));
            ForSurrogate(typeof(SurrogateForHashSet<>)).SetObject(x => ((ISurrogateRestorer)x).Restore());

            ForObject(typeof(SortedList<,>)).SetSurrogateGenericType(typeof(SurrogateForSortedList<,>));
            ForSurrogate(typeof(SurrogateForSortedList<,>)).SetObject(x => ((ISurrogateRestorer)x).Restore());
        }

        /// <summary>
        /// Serializes the specified object to a given stream.
        /// </summary>
        /// <param name='obj'>
        /// Object to serialize along with its references.
        /// </param>
        /// <param name='stream'>
        /// Stream to which the given object should be serialized. Has to be writeable.
        /// </param>
        /// <param name='metadataBytes'>
        /// Generic metadata describing the stream.
        /// </param>
        public void Serialize(object obj, Stream stream, byte[] metadataBytes = null)
        {
            WriteHeader(stream);
            if(metadataBytes != null)
            {
                WriteMetadata(stream, metadataBytes);   // Migrant does not allow serializing nulls
            }
            try
            {
                TouchWriter(stream);
                writer.WriteObject(obj);
            }
            finally
            {
                writer.Flush();
            }
            serializationDone = true;
        }

        /// <summary>
        /// Returns the open stream serializer, which can be used to do consecutive serializations
        /// </summary>
        /// <returns>The open stream serializer.</returns>
        /// <param name="stream">Stream.</param>
        /// <param name="metadataBytes">Generic metadata describing the stream.</param>
        public OpenStreamSerializer ObtainOpenStreamSerializer(Stream stream, byte[] metadataBytes = null)
        {
            WriteHeader(stream);
            if(metadataBytes != null)
            {
                WriteMetadata(stream, metadataBytes);   // Migrant does not allow serializing nulls
            }
            serializationDone = true;

            var result = new OpenStreamSerializer(CreateWriter(stream));
            return result;
        }

        /// <summary>
        /// Returns the open stream deserializer, which can be used to do consecutive deserializations when
        /// same technique was used to serialize data.
        /// </summary>
        /// <returns>The open stream deserializer.</returns>
        /// <param name="stream">Stream.</param>
        /// <param name="metadataBytes">Stream metadata.</param>
        public OpenStreamDeserializer ObtainOpenStreamDeserializer(Stream stream, out byte[] metadataBytes)
        {
            ThrowOnWrongResult(TryReadHeader(stream, out var preserveReferences, out var disableStamping));

            long streamPosition = stream.Position;
            var metadataResult = TryReadMetadata(stream, out metadataBytes);
            if(metadataResult != DeserializationResult.OK)
            {
                stream.Seek(streamPosition, SeekOrigin.Begin);
            }
            
            if(!settings.UseBuffering)
            {
                stream = new PeekableStream(stream);
            }
            var result = new OpenStreamDeserializer(CreateReader(stream), settings, stream);
            return result;
        }

        /// <summary>
        /// Gives the ability to set callback providing object for surrogate of given type. The object will be provided instead of such
        /// surrogate in the effect of deserialization.
        /// </summary>
        /// <returns>
        /// Object letting you set the object for the given surrogate type.
        /// </returns>
        /// <typeparam name='TSurrogate'>
        /// The type for which callback will be invoked.
        /// </typeparam>
        public ObjectForSurrogateSetter<TSurrogate> ForSurrogate<TSurrogate>()
        {
            return new ObjectForSurrogateSetter<TSurrogate>(this);
        }

        /// <summary>
        /// Gives the ability to set callback providing object for surrogate of given type. The object will be provided instead of such
        /// surrogate in the effect of deserialization.
        /// </summary>
        /// <returns>
        /// Object letting you set the object for the given surrogate type.
        /// </returns>
        /// <param name="type">
        /// The type for which callback will be invoked.
        /// </param>
        public ObjectForSurrogateSetter ForSurrogate(Type type)
        {
            return new ObjectForSurrogateSetter(this, type);
        }

        /// <summary>
        /// Gives the ability to set callback providing surrogate for objects of given type. The surrogate will be serialized instead of
        /// the object of that type.
        /// </summary>
        /// <returns>
        /// Object letting you set the surrogate for the given type.
        /// </returns>
        /// <typeparam name='TObject'>
        /// The type for which callback will be invoked.
        /// </typeparam>
        public SurrogateForObjectSetter<TObject> ForObject<TObject>()
        {
            return new SurrogateForObjectSetter<TObject>(this);
        }

        /// <summary>
        /// Gives the ability to set callback providing surrogate for objects of given type. The surrogate will be serialized instead of
        /// the object of that type.
        /// </summary>
        /// <returns>
        /// Object letting you set the surrogate for the given type.
        /// </returns>
        /// <param name="type">
        /// The type for which callback will be invoked.
        /// </param>
        public SurrogateForObjectSetter ForObject(Type type)
        {
            return new SurrogateForObjectSetter(this, type);
        }

        /// <summary>
        /// Deserializes object from the specified stream.
        /// </summary>
        /// <param name='stream'>
        /// The stream to read data from. Must be readable.
        /// </param>
        /// <typeparam name='T'>
        /// The expected type of the deserialized object. The deserialized object must be
        /// convertible to this type.
        /// </typeparam>
        public T Deserialize<T>(Stream stream)
        {
            ThrowOnWrongResult(TryDeserialize(stream, out T result));
            return result;
        }

        /// <summary>
        /// Deserializes object from the specified stream.
        /// </summary>
        /// <param name='stream'>
        /// The stream to read data from. Must be readable.
        /// </param>
        /// <param name="metadataBytes">Stream metadata.</param>
        /// <typeparam name='T'>
        /// The expected type of the deserialized object. The deserialized object must be
        /// convertible to this type.
        /// </typeparam>
        public T Deserialize<T>(Stream stream, out byte[] metadataBytes)
        {
            ThrowOnWrongResult(TryDeserialize(stream, out T result, out metadataBytes));
            return result;
        }

        /// <summary>
        /// Tries to deserialize object from specified stream.
        /// </summary>
        /// <returns>Operation result status.</returns>
        /// <param name="stream">
        /// The stream to read data from. Must be readable.
        /// </param>
        /// <param name="obj">Deserialized object.</param>
        /// <typeparam name="T">
        /// The expected type of the deserialized object. The deserialized object must be
        /// convertible to this type.
        /// </typeparam>
        public DeserializationResult TryDeserialize<T>(Stream stream, out T obj) 
        {
            return TryDeserialize(stream, out obj, out var _);
        }

        /// <summary>
        /// Tries to deserialize object from specified stream.
        /// </summary>
        /// <returns>Operation result status.</returns>
        /// <param name="stream">
        /// The stream to read data from. Must be readable.
        /// </param>
        /// <param name="obj">Deserialized object.</param>
        /// <param name="metadataBytes">Stream metadata.</param>
        /// <typeparam name="T">
        /// The expected type of the deserialized object. The deserialized object must be
        /// convertible to this type.
        /// </typeparam>
        public DeserializationResult TryDeserialize<T>(Stream stream, out T obj, out byte[] metadataBytes)
        {
            bool unused;
            bool disableStamping;
            obj = default(T);
            metadataBytes = null;

            var headerResult = TryReadHeader(stream, out unused, out disableStamping);
            if(headerResult != DeserializationResult.OK)
            {
                return headerResult;
            }
            if(disableStamping != settings.DisableTypeStamping)
            {
                return DeserializationResult.WrongStreamConfiguration;
            }

            bool readMetadata = false;
            long streamPosition = stream.Position;

            var metadataResult = TryReadMetadata(stream, out metadataBytes);
            if(metadataResult != DeserializationResult.OK)
            {
                stream.Seek(streamPosition, SeekOrigin.Begin);
            }
            else
            {
                readMetadata = true;
            }

            TouchReader(stream);

            try
            {
                obj = reader.ReadObject<T>();
                deserializationDone = true;
                return DeserializationResult.OK;
            }
            catch(VersionToleranceException ex)
            {
                LastException = ex;
                return DeserializationResult.TypeStructureChanged;
            }
            catch(Exception ex)
            {
                LastException = ex;
                return readMetadata ? DeserializationResult.StreamCorrupted : DeserializationResult.MetadataCorrupted;
            }
            finally
            {
                reader.Flush();
            }

        }

        /// <summary>
        /// Is invoked before serialization, once for every unique, serialized object. Provides this
        /// object in its single parameter.
        /// </summary>
        public event Action<object> OnPreSerialization;

        /// <summary>
        /// Is invoked after serialization, once for every unique, serialized object. Provides this
        /// object in its single parameter.
        /// </summary>
        public event Action<object> OnPostSerialization;

        /// <summary>
        /// Is invoked before deserialization, once for every unique, serialized object. Provides this
        /// object in its single parameter.
        /// </summary>
        public event Action<object> OnPostDeserialization;

        /// <summary>
        /// Makes a deep copy of a given object using the serializer.
        /// </summary>
        /// <returns>
        /// The deep copy of a given object.
        /// </returns>
        /// <param name='toClone'>
        /// The object to make a deep copy of.
        /// </param>
        /// <param name='settings'>
        /// Settings used for serializer which does deep clone.
        /// </param>
        public static T DeepClone<T>(T toClone, Settings settings = null)
        {
            var serializer = new Serializer(settings);
            var stream = new MemoryStream();
            serializer.Serialize(toClone, stream);
            var position = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            #if DEBUG_FORMAT
            if(DumpStream)
            {
                using(var f = File.Open("stream.dump", FileMode.Create))
                {
                    stream.WriteTo(f);
                }
            }
            stream.Seek(0, SeekOrigin.Begin);
            #endif
            var result = serializer.Deserialize<T>(stream);
            if(position != stream.Position)
            {
                throw new InvalidOperationException(
                    string.Format("Internal error in serializer: {0} bytes were written, but only {1} were read.",
                        position, stream.Position));
            }
            return result;
        }

        private void WriteHeader(Stream stream)
        {
            stream.WriteByte(Magic1);
            stream.WriteByte(Magic2);
            stream.WriteByte(Magic3);
            stream.WriteByte(VersionNumber);
            stream.WriteByte(settings.ReferencePreservation == ReferencePreservation.DoNotPreserve ? (byte)0 : (byte)1);
            stream.WriteByte(settings.DisableTypeStamping ? (byte)0 : (byte)1);
        }

        private byte ChecksumBytes(byte[] bytes)
        {
            return (byte)bytes.Aggregate(0x00, (acc, b) => acc ^ b);
        }

        private void WriteMetadata(Stream stream, byte[] metadataBytes)
        {
            if(metadataBytes.Length > MaximumMetadataLength)
            {
                throw new ArgumentException($"Metadata should be at most {MaximumMetadataLength} bytes long.");
            }

            stream.WriteByte((byte)metadataBytes.Length);
            stream.Write(metadataBytes, 0, metadataBytes.Length);

            // Append single-byte checksum - allows for a simple check if metadata was corrupted and a way to discern old format streams from the new format
            // (if the checksum doesn't match, either the metadata is corrupted or the stream is in the old format)
            var checksum = ChecksumBytes(metadataBytes);
            stream.WriteByte(checksum);
        }

        private void TouchWriter(Stream stream)
        {
            if(writer != null)
            {
                writer.ReuseWithNewStream(stream);
                return;
            }
            writer = CreateWriter(stream);
        }

        private ObjectWriter CreateWriter(Stream stream)
        {
            WriteMethods writeMethods;

            if(settings.SerializationMethod == Method.Generated)
            {
                writeMethods.writeMethodsProvider = new DynamicMethodProvider<WriteMethodDelegate>(t =>
                {
                    var specialWrite = ObjectWriter.LinkSpecialWrite(t);
                    if(specialWrite != null)
                    {
                        return specialWrite;
                    }

                    var generator = new WriteMethodGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                writeMethods.handleNewReferenceMethodsProvider = new DynamicMethodProvider<HandleNewReferenceMethodDelegate>(t =>
                {
                    var generator = new HandleNewReferenceMethodGenerator(t, objectsForSurrogates, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject, OnPreSerialization != null, OnPostSerialization != null);
                    return generator.Generate();
                });

                writeMethods.surrogateObjectIfNeededMethodsProvider = new DynamicMethodProvider<SurrogateObjectIfNeededDelegate>(t =>
                {
                    var generator = new SurrogateObjectIfNeededMethodGenerator(t, surrogatesForObjects, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                writeMethods.writeReferenceMethodsProvider = new DynamicMethodProvider<WriteReferenceMethodDelegate>(t =>
                {
                    var generator = new WriteReferenceMethodGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                writeMethods.callPostSerializationHooksMethodsProvider = new DynamicMethodProvider<CallPostSerializationHooksMethodDelegate>(t =>
                {
                    var generator = new CallPostSerializationHooksMethodGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });
            }
            else
            {
                writeMethods = GetReflectionBasedWriteMethods();
            }

            return new ObjectWriter(stream, writeMethods, OnPreSerialization, OnPostSerialization,
                             surrogatesForObjects, objectsForSurrogates, settings.TreatCollectionAsUserObject,
                             settings.UseBuffering, settings.DisableTypeStamping, settings.ReferencePreservation);
        }

        internal static WriteMethods GetReflectionBasedWriteMethods()
        {
            WriteMethods writeMethods;
            writeMethods.writeMethodsProvider = new DynamicMethodProvider<WriteMethodDelegate>(t =>
            {
                var specialWrite = ObjectWriter.LinkSpecialWrite(t);
                return specialWrite ?? ObjectWriter.WriteObjectUsingReflection;
            });

            writeMethods.handleNewReferenceMethodsProvider = new DynamicMethodProvider<HandleNewReferenceMethodDelegate>(t =>
            {
                return (ow, o, refId) => ow.HandleNewReference(o, refId);
            });

            writeMethods.surrogateObjectIfNeededMethodsProvider = new DynamicMethodProvider<SurrogateObjectIfNeededDelegate>(t =>
            {
                return (ow, o, refId) => ow.SurrogateObjectIfNeeded(o, refId);
            });

            writeMethods.writeReferenceMethodsProvider = new DynamicMethodProvider<WriteReferenceMethodDelegate>(t =>
            {
                return (ow, o) => ow.CheckLegalityAndWriteDeferredReference(o);
            });

            writeMethods.callPostSerializationHooksMethodsProvider = new DynamicMethodProvider<CallPostSerializationHooksMethodDelegate>(t =>
            {
                return (ow, o) => ow.CallPostSerializationHooksUsingReflection(o);
            });
            return writeMethods;
        }

        private DeserializationResult TryReadHeader(Stream stream, out bool preserveReferences, out bool disableStamping)
        {
            preserveReferences = false;
            disableStamping = false;

            // Read header
            var magic1 = stream.ReadByte();
            var magic2 = stream.ReadByte();
            var magic3 = stream.ReadByte();
            if(magic1 != Magic1 || magic2 != Magic2 || magic3 != Magic3)
            {
                return DeserializationResult.WrongMagic;
            }
            var version = stream.ReadByte();
            if(version != VersionNumber)
            {
                return DeserializationResult.WrongVersion;
            }
            preserveReferences = stream.ReadByteOrThrow() != 0;
            disableStamping = stream.ReadByteOrThrow() == 0;
            return DeserializationResult.OK;
        }

        private DeserializationResult TryReadMetadata(Stream stream, out byte[] metadataBytes)
        {
            metadataBytes = null;

            // Read number of bytes - 0 is invalid
            var metadataLength = stream.ReadByteOrThrow();
            if(metadataLength == 0)
            {
                return DeserializationResult.MetadataCorrupted;
            }

            // Read actual metadata
            var buffer = new byte[metadataLength];
            stream.Read(buffer, 0, metadataLength);

            // Read checksum and verify if metadata is correct
            // If it's incorrect, we could be deserializing a stream in the old format or the metadata is simply corrupted
            var checksum = stream.ReadByteOrThrow();
            if(ChecksumBytes(buffer) != checksum)
            {
                return DeserializationResult.MetadataCorrupted;
            }

            metadataBytes = buffer;
            return DeserializationResult.OK;
        }

        private void ThrowOnWrongResult(DeserializationResult result)
        {
            switch(result)
            {
            case DeserializationResult.OK:
                return;
            case DeserializationResult.WrongMagic:
                throw new InvalidOperationException("Cound not find proper magic.");
            case DeserializationResult.WrongVersion:
                throw new InvalidOperationException("Could not deserialize data serialized with another version of serializer.");
            case DeserializationResult.WrongStreamConfiguration:
                throw new InvalidOperationException("Type stamping configuration does not match.");
            case DeserializationResult.StreamCorrupted:
            case DeserializationResult.TypeStructureChanged:
                throw LastException;
            default:
                throw new ArgumentOutOfRangeException();
            }
        }

        private void TouchReader(Stream stream)
        {
            if(reader != null)
            {
                reader.ReuseWithNewStream(stream);
                return;
            }

            reader = CreateReader(stream);
        }

        private ObjectReader CreateReader(Stream stream)
        {
            ReadMethods readMethods = new ReadMethods();
            if(settings.DeserializationMethod == Method.Generated)
            {
                readMethods.readMethodsProvider = new DynamicMethodProvider<ReadMethodDelegate>(t =>
                {
                    var generator = new ReadMethodGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                readMethods.touchInlinedObjectMethodsProvider = new DynamicMethodProvider<TouchInlinedObjectMethodDelegate>(t =>
                {
                    var generator = new TouchInlinedObjectGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                readMethods.completeMethodsProvider = new DynamicMethodProvider<CompleteMethodDelegate>(t =>
                {
                    var generator = new CompletedGenerator(t, objectsForSurrogates, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject, OnPostDeserialization != null);
                    return generator.Generate();
                });

                readMethods.createObjectMethodsProvider = new DynamicMethodProvider<CreateObjectMethodDelegate>(t =>
                {
                    var generator = new CreateObjectGenerator(t, settings.DisableTypeStamping, settings.TreatCollectionAsUserObject);
                    return generator.Generate();
                });

                readMethods.cloneContentMehtodsProvider = new DynamicMethodProvider<CloneMethodDelegate>(t =>
                {
                    if(t.IsValueType)
                    {
                        return ObjectReader.CloneContentUsingReflection;
                    }
                    var generator = new CloneContentMethodGenerator(t);
                    return generator.Generate();
                });
            }
            else
            {
                readMethods = GetReflectionBasedReadMethods(settings.TreatCollectionAsUserObject);
            }

            return new ObjectReader(stream, readMethods, objectsForSurrogates, OnPostDeserialization,
                settings.TreatCollectionAsUserObject, settings.VersionTolerance, settings.UseBuffering,
                settings.DisableTypeStamping, settings.ReferencePreservation, settings.ForceStampVerification);
        }

        internal static ReadMethods GetReflectionBasedReadMethods(bool treatCollectionAsUserObject)
        {
            ReadMethods readMethods = new ReadMethods();
            readMethods.readMethodsProvider = new DynamicMethodProvider<ReadMethodDelegate>(t =>
            {
                return ObjectReader.ReadObjectInnerUsingReflection;
            });

            readMethods.touchInlinedObjectMethodsProvider = new DynamicMethodProvider<TouchInlinedObjectMethodDelegate>(t =>
            {
                return (or, refId) => or.TryTouchInlinedObjectUsingReflection(t, refId);
            });

            readMethods.completeMethodsProvider = new DynamicMethodProvider<CompleteMethodDelegate>(t =>
            {
                return (or, refId) => or.CompletedInnerUsingReflection(refId);
            });

            readMethods.createObjectMethodsProvider = new DynamicMethodProvider<CreateObjectMethodDelegate>(t =>
            {
                return () => ObjectReader.CreateObjectUsingReflection(t, treatCollectionAsUserObject);
            });
            return readMethods;
        }

        internal static object CreateGenericSurrogateUsingReflection(Type t, object obj)
        {
            var finalType = t.MakeGenericType(obj.GetType().GetGenericArguments());
            return Activator.CreateInstance(finalType, obj);
        }

        /// <summary>
        /// Returns the last serializer exception.
        /// </summary>
        public Exception LastException { get; private set; }

        /// <summary>
        /// Returns the current serializer settings.
        /// </summary>
        public Settings Settings => settings;

        private bool serializationDone;
        private bool deserializationDone;
        private ObjectWriter writer;
        private ObjectReader reader;
        private readonly Settings settings;
        internal readonly SwapList surrogatesForObjects;
        private readonly SwapList objectsForSurrogates;
        private const byte VersionNumber = 9;
        private const byte Magic1 = 0x32;
        private const byte Magic2 = 0x66;
        private const byte Magic3 = 0x34;
        private const byte MaximumMetadataLength = 255;

        #if DEBUG_FORMAT
        public static readonly bool DumpStream = true;
        public static readonly bool DisableVarints = true;
        public static readonly bool DisableBuffering = true;
        #endif

        internal delegate object CreateGenericSurrogateDelegate(Type t, object o);

        internal struct WriteMethods
        {
            public DynamicMethodProvider<CallPostSerializationHooksMethodDelegate> callPostSerializationHooksMethodsProvider;
            public DynamicMethodProvider<WriteReferenceMethodDelegate> writeReferenceMethodsProvider;
            public DynamicMethodProvider<SurrogateObjectIfNeededDelegate> surrogateObjectIfNeededMethodsProvider;
            public DynamicMethodProvider<HandleNewReferenceMethodDelegate> handleNewReferenceMethodsProvider;
            public DynamicMethodProvider<WriteMethodDelegate> writeMethodsProvider;
        }

        internal struct ReadMethods
        {
            public DynamicMethodProvider<ReadMethodDelegate> readMethodsProvider;
            public DynamicMethodProvider<CompleteMethodDelegate> completeMethodsProvider;
            public DynamicMethodProvider<CreateObjectMethodDelegate> createObjectMethodsProvider;
            public DynamicMethodProvider<TouchInlinedObjectMethodDelegate> touchInlinedObjectMethodsProvider;
            public DynamicMethodProvider<CloneMethodDelegate> cloneContentMehtodsProvider;
        }

        /// <summary>
        /// Base class for surrogate setter.
        /// </summary>
        public class BaseObjectForSurrogateSetter
        {
            internal BaseObjectForSurrogateSetter(Serializer serializer)
            {
                Serializer = serializer;
            }

            internal void CheckLegality()
            {
                if(Serializer.deserializationDone)
                {
                    throw new InvalidOperationException("Cannot set objects for surrogates after any deserialization is done.");
                }
            }

            internal readonly Serializer Serializer;
        }

        /// <summary>
        /// Lets you set a callback providing object for type of the surrogate given to method that provided
        /// this object on a serializer that provided this object.
        /// </summary>
        public sealed class ObjectForSurrogateSetter<TSurrogate> : BaseObjectForSurrogateSetter
        {
            internal ObjectForSurrogateSetter(Serializer serializer) : base(serializer)
            {
            }

            /// <summary>
            /// Sets the callback proividing object for surrogate.
            /// </summary>
            /// <param name='callback'>
            /// Callback proividing object for surrogate. The callback can be null, in that case surrogate of the type
            /// <typeparamref name="TSurrogate" /> will be deserialized as is even if there is an object for the more
            /// general type.
            /// </param>
            /// <typeparam name='TObject'>
            /// The type of the object returned by callback.
            /// </typeparam>
            public void SetObject<TObject>(Func<TSurrogate, TObject> callback) where TObject : class
            {
                CheckLegality();
                Serializer.objectsForSurrogates.AddOrReplace(typeof(TSurrogate), callback);
            }
        }

        /// <summary>
        /// Lets you set a callback providing object for type of the surrogate given to method that provided
        /// this object on a serializer that provided this object.
        /// </summary>
        public sealed class ObjectForSurrogateSetter : BaseObjectForSurrogateSetter
        {
            internal ObjectForSurrogateSetter(Serializer serializer, Type type) : base(serializer)
            {
                this.type = type;
            }

            /// <summary>
            /// Sets the callback proividing object for surrogate.
            /// </summary>
            /// <param name="callback">
            /// Callback proividing object for surrogate. The callback can be null, in that case surrogate of the
            /// appropriate type will be deserialized as is even if there is an object for the more general type.
            /// </param>
            public void SetObject(Func<object, object> callback)
            {
                CheckLegality();
                Serializer.objectsForSurrogates.AddOrReplace(type, callback);
            }

            private readonly Type type;
        }

        /// <summary>
        /// Base class for object setter.
        /// </summary>
        public class BaseSurrogateForObjectSetter
        {
            internal BaseSurrogateForObjectSetter(Serializer serializer)
            {
                this.Serializer = serializer;
            }

            internal void CheckLegality()
            {
                if(Serializer.serializationDone)
                {
                    throw new InvalidOperationException("Cannot set surrogates for objects after any serialization is done.");
                }
            }

            internal readonly Serializer Serializer;
        }

        /// <summary>
        /// Lets you set a callback providing surrogate for type of the object given to method that provided
        /// this object on a serializer that provided this object.
        /// </summary>
        public sealed class SurrogateForObjectSetter<TObject> : BaseSurrogateForObjectSetter
        {
            internal SurrogateForObjectSetter(Serializer serializer) : base(serializer)
            {

            }

            /// <summary>
            /// Sets the callback providing surrogate for object.
            /// </summary>
            /// <param name='callback'>
            /// Callback providing surrogate for object. The callback can be null, in that case object of the type
            /// <typeparamref name="TObject" /> will be serialized as is even if there is a surrogate for the more
            /// general type.
            /// </param>
            /// <typeparam name='TSurrogate'>
            /// The type of the object returned by callback.
            /// </typeparam>
            public void SetSurrogate<TSurrogate>(Func<TObject, TSurrogate> callback) where TSurrogate : class
            {
                CheckLegality();
                Serializer.surrogatesForObjects.AddOrReplace(typeof(TObject), callback);
            }
        }

        /// <summary>
        /// Lets you set a callback providing surrogate for type of the object given to method that provided
        /// this object on a serializer that provided this object.
        /// </summary>
        public sealed class SurrogateForObjectSetter : BaseSurrogateForObjectSetter
        {
            internal SurrogateForObjectSetter(Serializer serializer, Type type) : base(serializer)
            {
                this.type = type;
            }

            /// <summary>
            /// Sets the callback providing surrogate for object.
            /// </summary>
            /// <param name='callback'>
            /// Callback providing surrogate for object. The callback can be null, in that case object of the
            /// appropriate type will be serialized as is even if there is a surrogate for the more general type.
            /// </param>
            public void SetSurrogate(Func<object, object> callback)
            {
                CheckLegality();
                Serializer.surrogatesForObjects.AddOrReplace(type, callback);
            }

            /// <summary>
            /// Sets the generic surrogate type that should be used to replace the original one.
            ///
            /// It is required for the type to be generic, has the same amount of parameters as the original
            /// one and to has a constructor accepting one parameter of the original type.
            /// </summary>
            /// <param name="t">Generic type.</param>
            public void SetSurrogateGenericType(Type t)
            {
                if(!type.IsGenericType || !t.IsGenericType)
                {
                    throw new ArgumentException("Expected generic types");
                }

                if(type.GetGenericArguments().Length != t.GetGenericArguments().Length)
                {
                    throw new ArgumentException("Generic arguments count mismatch");
                }

                if(Serializer.settings.SerializationMethod == Method.Reflection)
                {
                    Serializer.surrogatesForObjects.AddOrReplace(type, new Func<object, object>(x =>
                    {
                        return CreateGenericSurrogateUsingReflection(t, x);
                    }));
                }
                else
                {
                    Serializer.surrogatesForObjects.AddGenericTemplate(type, new Func<Type, Delegate>(lt =>
                    {
                        var finalType = t.MakeGenericType(lt.GetGenericArguments());
                        var generator = new CreateGenericSurrogateMethodGenerator(lt, finalType);
                        return new Func<object, object>(o => generator.Generate()(null, o));
                    }));
                }
            }

            private readonly Type type;
        }

        /// <summary>
        /// Serializer that is attached to one stream and can do consecutive serializations that are aware of the data written
        /// by previous ones.
        /// </summary>
        public class OpenStreamSerializer : IDisposable
        {
            internal OpenStreamSerializer(ObjectWriter writer)
            {
                this.writer = writer;
            }

            /// <summary>
            /// Serialize the specified object.
            /// </summary>
            /// <param name="obj">Object.</param>
            public void Serialize(object obj)
            {
                writer.WriteObject(obj);
            }

            /// <summary>
            /// Flushes the buffer and necessary padding. Not necessary when buffering is not used.
            /// </summary>
            public void Dispose()
            {
                writer.Flush();
            }

            private readonly ObjectWriter writer;
        }

        /// <summary>
        /// Deserializer that is attached to one stream and can do consecutive deserializations that are aware of the data written
        /// by previous ones.
        /// </summary>
        public class OpenStreamDeserializer : IDisposable
        {
            internal OpenStreamDeserializer(ObjectReader reader, Settings settings, Stream stream)
            {
                this.reader = reader;
                this.settings = settings;
                this.stream = stream;
            }

            /// <summary>
            /// Deserializes next object waiting in the stream.
            /// </summary>
            /// <typeparam name="T">The expected formal type of object to deserialize.</typeparam>
            public T Deserialize<T>()
            {
                return reader.ReadObject<T>();
            }

            /// <summary>
            /// Deserializes objects until end of the stream is reached. All objects have to be castable to T.
            /// This method is only available when buffering is disabled.
            /// </summary>
            /// <returns>Lazy collection of deserialized objects.</returns>
            /// <typeparam name="T">The expected type of object to deserialize.</typeparam>
            public IEnumerable<T> DeserializeMany<T>()
            {
                if(settings.UseBuffering)
                {
                    throw new NotSupportedException("DeserializeMany can be only used if buffering is disabled.");
                }
                var peekableStream = stream as PeekableStream;
                if(peekableStream == null)
                {
                    throw new NotSupportedException("Internal error: stream is not peekable.");
                }
                while(peekableStream.Peek() != -1)
                {
                    yield return Deserialize<T>();
                }
            }

            /// <summary>
            /// Reads leftover padding. Not necessary if buffering is not used.
            /// </summary>
            public void Dispose()
            {
                reader.Flush();
            }

            private readonly ObjectReader reader;
            private readonly Settings settings;
            private readonly Stream stream;
        }
    }
}

