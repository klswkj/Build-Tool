using System;
using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	abstract class ToolChain
	{
		public ToolChain()
		{
		}

		public virtual void SetEnvironmentVariables()
		{
		}

		public virtual void GetVersionInfo(List<string> Lines)
		{
		}

		// Goto VCToolChain Line:918
		public abstract CPPOutput CompileCPPFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InputFiles, DirectoryReference OutputDir, string ModuleName, IActionGraphBuilder Graph);

		public abstract CPPOutput CompileRCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph);

		// Goto ISPC
		public abstract CPPOutput CompileISPCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph);

		// XXXToolChain, HololensToolChain, AndroidToolChain returns null.
		public abstract CPPOutput GenerateOnlyISPCHeaders(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph);

		// Go to VCToolChain.cs::GenerateTypeLibraryHeader Line:1436
		public virtual void GenerateTypeLibraryHeader(CppCompileEnvironment CompileEnvironment, ModuleRules.TypeLibrary TypeLibrary, FileReference OutputFile, IActionGraphBuilder Graph)
		{
			System.Diagnostics.Debugger.Break();
			throw new NotSupportedException("This platform does not support type libraries.");
		}

		public abstract FileItem LinkFiles(LinkEnvironment LinkEnvironment, bool bBuildStaticLib, IActionGraphBuilder Graph);
		public virtual FileItem[] LinkAllFiles(LinkEnvironment LinkEnvironment, bool bBuildStaticLib, IActionGraphBuilder Graph)
		{
			// FileItem LinkedItem = LinkFiles(LinkEnvironment, bBuildImportLibraryOnly, Graph);

			return new FileItem[] { LinkFiles(LinkEnvironment, bBuildStaticLib, Graph) };
		}

		// Get the name of the response file for the current linker environment and output file
		// <param name="LinkEnvironment"></param>
		// <param name="OutputFile"></param>
		// <returns></returns>
		public static FileReference GetResponseFileName(LinkEnvironment LinkEnvironment, FileItem OutputFile)
		{
			// Construct a relative path for the intermediate response file
			return FileReference.Combine(LinkEnvironment.IntermediateDirectory, OutputFile.FileDirectory.GetFileName() + ".response");
		}

		public virtual ICollection<FileItem> PostBuild(FileItem Executable, LinkEnvironment ExecutableLinkEnvironment, IActionGraphBuilder Graph)
		{
			return new List<FileItem>();
		}

		public virtual void SetUpGlobalEnvironment(ReadOnlyTargetRules Target)
		{
		}

		public virtual void ModifyBuildProducts(ReadOnlyTargetRules Target, BuildBinary Binary, List<string> Libraries, List<BuildBundleResource> BundleResources, Dictionary<FileReference, BuildProductType> BuildProducts)
		{
		}

		public virtual void FinalizeOutput(ReadOnlyTargetRules Target, TargetMakefile Makefile)
		{
		}

		public virtual void PrepareRuntimeDependencies( List<RuntimeDependency> RuntimeDependencies, Dictionary<FileReference, FileReference> TargetFileToSourceFile, DirectoryReference ExeDir )
		{
		}

		// Adds a build product and its associated debug file to a receipt.
		// <param name="OutputFile">Build product to add</param>
		// <param name="OutputType">The type of build product</param>
		public virtual bool ShouldAddDebugFileToReceipt(FileReference OutputFile, BuildProductType OutputType)
		{
			return true;
		}
		
		public virtual FileReference GetDebugFile(FileReference OutputFile, string DebugExtension)
		{
			//  by default, just change the extension to the debug extension
			return OutputFile.ChangeExtension(DebugExtension);
		}

		public virtual void SetupBundleDependencies(List<BuildBinary> Binaries, string GameName)
		{
		}

        public virtual string GetSDKVersion()
        {
            return "Not Applicable";
        }
	};
}
