using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// 여기 PCHTemplate의 이해가 필수.
	// => 1. Definitions, PCH Include 하는것을 잘못 판단함.
	// 2. PCH앞뒤로 붙는 Prefix, Suffix뜻 의미 찾는 중
	// 3. Optimized, NonOptimized, RTTI, NonRTTI, Exceptions, NoExceptions, ShadowErrors, ShadowWarnings,
	//    NoShadow, TypeCastErrors, TypeCastWarnings, NoTypeCast, .Undef, NoUndef이 어떻게 이루어지는지 확인
	// 이 해결가능성 커짐.

	// PCHTemplate <=> PCHInstance <=> PCHManifest

	// Shared는 *.target.cs 정해지는건데, NonOptimized, RTTI, NonRTTI, 

	// A template for creating a shared PCH.
	// Instances of it are created depending on the configurations required.
	internal sealed class PCHTemplate
	{
		public BuildModuleCPP ModuleWithValidSharedPCH; // Module providing this PCH.

		// Including all the public compile environment that all consuming modules inherit.
		public CppCompileEnvironment BaseCompileEnvironmentToUse;

		public FileItem PCHFile;
		public DirectoryReference OutputDirForThisPCH;
		public List<PCHInstance> PCHInstances = new List<PCHInstance>();

		public PCHTemplate(BuildModuleCPP Module, CppCompileEnvironment BaseCompileEnvironment, FileItem HeaderFile, DirectoryReference OutputDir)
		{
			this.ModuleWithValidSharedPCH    = Module;
			this.BaseCompileEnvironmentToUse = BaseCompileEnvironment;
			this.PCHFile                     = HeaderFile;
			this.OutputDirForThisPCH         = OutputDir;
		}

		// Checks whether this template is valid for the given compile environment
		public bool IsValidFor(CppCompileEnvironment CompileEnvironment)
		{
			if(CompileEnvironment.bIsBuildingDLL != BaseCompileEnvironmentToUse.bIsBuildingDLL)
			{
				return false;
			}
			if(CompileEnvironment.bIsBuildingLibrary != BaseCompileEnvironmentToUse.bIsBuildingLibrary)
			{
				return false;
			}

			return true;
		}

		public override string ToString() => PCHFile.AbsolutePath;
	}
}
