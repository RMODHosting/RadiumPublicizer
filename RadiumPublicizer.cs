using Mono.Cecil;
using Mono.Collections.Generic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RadiumPublicizer
{
    public static class RadiumPublicizer
    {
        public const string SUFFIX = "-Publicized";

        public static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("You must specify an input path followed by an output path.");
                return;
            }

            var inputPath = Path.GetFullPath(args[0]);
            var outputPath = Path.GetFullPath(args[1]);

            var files = new List<string>();
            files.AddRange(Directory.EnumerateFiles(inputPath, "*.dll", SearchOption.TopDirectoryOnly));

            Directory.Delete(outputPath, true);

            Parallel.ForEach(files, file =>
            {
                var filename = Path.GetFileName(file);
                if (!ShouldPublicize(filename))
                    return;

                using var publicizer = new Publicizer(file);
                var result = publicizer.Run();

                Directory.CreateDirectory(outputPath);
                publicizer.Write(outputPath + filename);

                PrintResult(filename, result);
            });
        }


        public static bool ShouldPublicize(string filename)
        {
            if (filename.Contains("Oxide.References")) return false;

            if (filename == "NewAssembly.dll") return true;
            if (filename.Contains("Apex")) return true;
            if (filename.Contains("Assembly-CSharp")) return true;
            if (filename.Contains("Facepunch")) return true;
            if (filename.Contains("Rust")) return true;
            if (filename.Contains("Oxide")) return true;

            return false;
        }


        private static void PrintResult(string path, Publicizer.PublicizeResult result)
        {
            Console.WriteLine($"Publicized {path} - Types: {result.Types}, NestedTypes: {result.NestedTypes}, Events: {result.Events}, Fields: {result.Fields}, Methods: {result.Methods}, Setters: {result.Property_Setters}, Getters: {result.Property_Getters}");
        }

        public static ReaderParameters GetReaderParameters() => new(ReadingMode.Immediate)
        {
            InMemory = true
        };
    }

    public sealed class Publicizer : IDisposable
    {
        public sealed class PublicizeResult
        {
            public uint Types;
            public uint NestedTypes;
            public uint Events;
            public uint Fields;
            public uint Methods;

            public uint Property_Setters;
            public uint Property_Getters;

            public void BumpTypes() => ++Types;
            public void BumpNestedTypes() => ++NestedTypes;
            public void BumpEvents() => ++Events;
            public void BumpFields() => ++Fields;
            public void BumpMethods() => ++Methods;
            public void BumpPropertySetters() => ++Property_Setters;
            public void BumpPropertyGetters() => ++Property_Getters;
        }

        private readonly AssemblyDefinition _assembly;
        private Collection<TypeDefinition>? _types;

        public Publicizer(string path)
        {
            var rParams = RadiumPublicizer.GetReaderParameters();
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            rParams.AssemblyResolver = resolver;

            _assembly = AssemblyDefinition.ReadAssembly(path, rParams);
        }

        public PublicizeResult Run()
        {
            var result = new PublicizeResult();
            DoPublicize(result);
            return result;
        }

        public void Write(string path) => _assembly.Write(path);

        private void DoPublicize(PublicizeResult value)
        {
            DoPublicizeTypes(value);
            DoPublicizeNestedTypes(value);

            DoPublicizeFields(value);
            // Publicize events before publicizing methods cuz events are literally two methods & one field
            DoFixupEvents(value);
            // Publicize properties before publicizing methods cuz setters/getters are methods
            DoPublicizePropertySetters(value);
            DoPublicizePropertyGetters(value);

            DoPublicizeMethods(value);
        }

        #region Utility

        private Collection<TypeDefinition> GetTypes()
        {
            if (_types is not null)
                return _types;

            var coll = new Collection<TypeDefinition>();
            void AddTypes(Collection<TypeDefinition> definitions)
            {
                for (var z = 0; z < definitions.Count; z++)
                {
                    var definition = definitions[z];
                    coll.Add(definition);
                    AddTypes(definition.NestedTypes);
                }
            }

            AddTypes(_assembly.MainModule.Types);

            return _types = coll;
        }

        private void Processor<T>(Collection<T> values, Predicate<T> filter, Action<T> processor, Action bump)
        {
            for (var z = 0; z < values.Count; z++)
            {
                var d = values[z];
                if (filter(d))
                {
                    processor(d);
                    bump();
                }
            }
        }

        private void ArrayProcessor<T, R>(Collection<T> values, Func<T, Collection<R>> getter, Action<Collection<R>> processor)
        {
            for (var z = 0; z < values.Count; z++)
            {
                var d = values[z];
                var coll = getter(d);
                processor(coll);
            }
        }

        #endregion

        #region Do

        private void DoPublicizeTypes(PublicizeResult value) =>
            Processor<TypeDefinition>(GetTypes(),
                (t) => !t.IsNested && !t.IsPublic,
                (t) => t.IsPublic = true,
                value.BumpTypes);

        private void DoPublicizeNestedTypes(PublicizeResult value) =>
            Processor<TypeDefinition>(GetTypes(),
                (nt) => nt.IsNested && !nt.IsNestedPublic,
                (nt) => nt.IsNestedPublic = true,
                value.BumpNestedTypes);

        private void DoPublicizeFields(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, FieldDefinition>(GetTypes(),
                (t) => t.Fields,
                (fs) => Processor<FieldDefinition>(fs,
                    (f) => !f.IsPublic,
                    (f) => f.IsPublic = true,
                    value.BumpFields));

        private void DoPublicizeMethods(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, MethodDefinition>(GetTypes(),
                (t) => t.Methods,
                (ms) => Processor<MethodDefinition>(ms,
                    (m) => !m.IsPublic,
                    (m) => m.IsPublic = true,
                    value.BumpMethods));

        private void DoPublicizePropertySetters(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, PropertyDefinition>(GetTypes(),
                (t) => t.Properties,
                (ps) => Processor<PropertyDefinition>(ps,
                    (p) => !p.SetMethod?.IsPublic ?? false,
                    (p) => p.SetMethod.IsPublic = true,
                    value.BumpPropertySetters));

        private void DoPublicizePropertyGetters(PublicizeResult value) =>
                        ArrayProcessor<TypeDefinition, PropertyDefinition>(GetTypes(),
                (t) => t.Properties,
                (ps) => Processor<PropertyDefinition>(ps,
                    (p) => !p.GetMethod?.IsPublic ?? false,
                    (p) => p.GetMethod.IsPublic = true,
                    value.BumpPropertyGetters));

        private void DoFixupEvents(PublicizeResult value)
        {
            bool FilterEvent(EventDefinition e)
            {
                // Sometimes, for some reason, events have the same name as their backing fields.
                // If both are public, neither can be accessed (name conflict).
                var backing = e.DeclaringType.Fields.SingleOrDefault(f => f.Name == e.Name);
                if (backing != null)
                {
                    backing.IsPrivate = true;
                    value.Fields--;
                }

                return e.AddMethod.IsPrivate || e.RemoveMethod.IsPrivate || (e.InvokeMethod?.IsPrivate ?? false);
            }

            void ProcessEvent(EventDefinition e)
            {
                e.AddMethod.IsPublic = true;
                e.RemoveMethod.IsPublic = true;

                if (e.InvokeMethod != null)
                    e.InvokeMethod.IsPublic = true;
            }

            ArrayProcessor<TypeDefinition, EventDefinition>(GetTypes(),
                (t) => t.Events,
                (es) => Processor<EventDefinition>(es,
                    FilterEvent,
                    ProcessEvent,
                    value.BumpEvents));
        }

        #endregion

        public void Dispose() => _assembly.Dispose();
    }
}
