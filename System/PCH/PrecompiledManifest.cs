using System;
using System.Collections.Generic;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores information about a compiled binary or module,
	// including the build products and intermediate folders.
	internal sealed class PrecompiledManifest
	{
		// List of files produced by compiling the module.
		// These are within the module output directory.
		public List<FileReference> OutputFiles = new List<FileReference>();

		// Read a receipt from disk.
		public static PrecompiledManifest Read(FileReference FileToRead)
		{
			PrecompiledManifest OutManifest = new PrecompiledManifest();
			DirectoryReference  BaseDir     = FileToRead.Directory;
			JsonObject          RawObject   = JsonObject.Read(FileToRead);
			string[]            OutputFiles = RawObject.GetStringArrayField(nameof(OutputFiles));

			foreach(string OutputFile in OutputFiles)
			{
				OutManifest.OutputFiles.Add(FileReference.Combine(BaseDir, OutputFile));
			}

			return OutManifest;
		}

		// Try to read a manifest from disk, failing gracefully if it can't be read.
		public static bool TryRead(FileReference FileToRead, out PrecompiledManifest OutPCHManifest)
		{
			if (!FileReference.Exists(FileToRead))
			{
				OutPCHManifest = null;
				return false;
			}

			try
			{
				OutPCHManifest = Read(FileToRead);
				return true;
			}
			catch (Exception)
			{
				OutPCHManifest = null;
				return false;
			}
		}

		// Write the receipt to disk.
		public void WriteIfModified(FileReference OutputFileToWrite)
		{
			DirectoryReference BaseDir = OutputFileToWrite.Directory;

			MemoryStream MemoryStream = new MemoryStream();
			using (JsonWriter Writer = new JsonWriter(new StreamWriter(MemoryStream)))
			{
				Writer.WriteObjectStart();

				string[] OutputFileStrings = new string[OutputFiles.Count];
				for(int Idx = 0; Idx < OutputFiles.Count; ++Idx)
				{
					OutputFileStrings[Idx] = OutputFiles[Idx].MakeRelativeTo(BaseDir);
				}
				Writer.WriteStringArrayField(nameof(OutputFiles), OutputFileStrings);

				Writer.WriteObjectEnd();
			}

			FileReference.WriteAllBytesIfDifferent(OutputFileToWrite, MemoryStream.ToArray());
		}

		// Default constructor
		public PrecompiledManifest()
		{
		}
	}
}
