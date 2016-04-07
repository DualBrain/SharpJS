﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JSIL.Internal;
using JSIL.Translator;
using Mono.Cecil;

namespace JSIL {
    public class TranslationResult {
        public struct ResultFile {
            public string Type;
            public string Filename;
            public long Size;
            public ArraySegment<byte> Contents;
            public Dictionary<string, object> Properties;
            public SourceMapBuilder SourceMapBuilder;
        }

        public readonly string AssemblyPath;
        public readonly AssemblyManifest AssemblyManifest;

        public readonly Configuration Configuration;
        public readonly List<AssemblyDefinition> Assemblies = new List<AssemblyDefinition>();
        public readonly List<string> FileOrder = new List<string>();
        public readonly Dictionary<string, ResultFile> Files = new Dictionary<string, ResultFile>();
        public readonly StringBuilder Log = new StringBuilder();
        public TimeSpan Elapsed;
        public ArraySegment<byte> Manifest;

        internal TranslationResult (
            Configuration configuration, string assemblyPath, AssemblyManifest assemblyManifest
        ) {
            Configuration = configuration;
            AssemblyPath = assemblyPath;
            AssemblyManifest = assemblyManifest;
        }

        public IEnumerable<ResultFile> OrderedFiles {
            get {
                foreach (var filename in FileOrder)
                    yield return Files[filename];
            }
        }

        public void AddFile (
            string type, 
            string filename, 
            ArraySegment<byte> bytes, 
            int? position = null,
            Dictionary<string, object> properties = null,
            SourceMapBuilder sourceMapBuilder = null
        ) {
            lock (Files) {
                if (position.HasValue)
                    FileOrder.Insert(position.Value, filename);
                else
                    FileOrder.Add(filename);

                Files.Add(filename, new ResultFile {
                    Type = type,
                    Filename = filename,
                    Contents = bytes,
                    Size = bytes.Count,
                    Properties = properties,
                    SourceMapBuilder = sourceMapBuilder
                });
            }
        }

        public void WriteToStream (Stream output) {
            if (Manifest.Array == null)
                throw new Exception("AssemblyTranslator.GenerateManifest must be called first");

            var newline = Encoding.ASCII.GetBytes(Environment.NewLine);

            output.Write(Manifest.Array, Manifest.Offset, Manifest.Count);
            output.Write(newline, 0, newline.Length);

            foreach (var file in Files.Values) {
                if (file.Contents.Count > 0) {
                    output.Write(file.Contents.Array, file.Contents.Offset, file.Contents.Count);
                }
                output.Write(newline, 0, newline.Length);
            }

            output.Flush();
        }

        public string WriteToString () {
            if (Manifest.Array == null)
                throw new Exception("AssemblyTranslator.GenerateManifest must be called first");

            using (var ms = new MemoryStream(AssemblyTranslator.DefaultStreamCapacity)) {
                WriteToStream(ms);
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        private static void WriteBytesToFile (string folder, string name, ArraySegment<byte> bytes, SourceMapBuilder sourceMapBuilder = null) {
            var filePath = Path.Combine(folder, name);
            var fileMode = File.Exists(filePath) ? FileMode.Truncate : FileMode.CreateNew;

            EnsureDirectoryExists(Path.GetDirectoryName(filePath));

            bool writeMapLink = sourceMapBuilder != null && sourceMapBuilder.Build(folder, name);
            using (var fs = File.Open(filePath, fileMode, FileAccess.Write, FileShare.Read))
            {
                fs.Write(bytes.Array, bytes.Offset, bytes.Count);
                if (writeMapLink)
                    sourceMapBuilder.WriteSourceMapLink(fs, folder, name);
                fs.Flush();
            }
        }

        public static void EnsureDirectoryExists (string directoryName) {
            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);
        }

        public void WriteToDirectory (string path, string manifestPrefix = "") {
            if (Manifest.Array == null)
                throw new Exception("AssemblyTranslator.GenerateManifest must be called first");

            EnsureDirectoryExists(path);

            WriteBytesToFile(path, manifestPrefix + "manifest.js", Manifest);

            foreach (var kvp in Files) {
                if (kvp.Value.Contents.Count > 0)
                    WriteBytesToFile(path, kvp.Key, kvp.Value.Contents, kvp.Value.SourceMapBuilder);
            }
        }

        internal void AddExistingFile (string type, string filename, long fileSize, int? position = null) {
            lock (Files) {
                if (Files.ContainsKey(filename)) {
                    var existingFile = Files[filename];
                    if (
                        (existingFile.Size != fileSize) ||
                        (existingFile.Type != type) ||
                        (existingFile.Contents.Count > 0)
                    ) {
                        throw new InvalidOperationException(String.Format(
                            "A '{0}' named '{1}' already exists in the asset manifest, and has different metadata than the file being added.",
                            type, filename
                        ));
                    } else {
                        Console.Error.WriteLine("// Warning: Adding '{0}' '{1}' to manifest multiple times!", type, filename);
                    }

                    return;
                }

                if (position.HasValue)
                    FileOrder.Insert(position.Value, filename);
                else
                    FileOrder.Add(filename);

                Files.Add(filename, new ResultFile {
                    Type = type,
                    Filename = filename,
                    Size = fileSize
                });
            }
        }
    }
}
