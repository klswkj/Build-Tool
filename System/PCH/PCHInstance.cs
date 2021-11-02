using BuildToolUtilities;

namespace BuildTool
{
	// Information about a PCH instance
	internal sealed class PCHInstance
	{
		public CPPOutput             CppOutputFiles;     // The output files for the shared PCH { ObjectFiles, DebugDataFiles, GeneratedHeaderFiles, PrecompiledHeaderFile }
		public FileItem              HeaderFile;         // The file to include to use this shared PCH
		public CppCompileEnvironment CompileEnvironment; // The compile environment for this shared PCH

		// Return a string representation of this object for debugging
		// <returns>String representation of the object</returns>
		public override string ToString()
		{
			return HeaderFile.FileDirectory.GetFileName();
		}

		public PCHInstance(FileItem HeaderFile, CppCompileEnvironment CompileEnvironment, CPPOutput Output)
		{
			this.HeaderFile         = HeaderFile;
			this.CompileEnvironment = CompileEnvironment;
			this.CppOutputFiles     = Output;
		}
	}
}
