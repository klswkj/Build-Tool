using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	class WindowsProjectGenerator : PlatformProjectGenerator
	{
		public WindowsProjectGenerator(CommandLineArguments Arguments)
			: base(Arguments)
		{
		}

		public override IEnumerable<BuildTargetPlatform> GetPlatforms()
		{
			yield return BuildTargetPlatform.Win32;
			yield return BuildTargetPlatform.Win64;
		}

		public override string GetVisualStudioPlatformName(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration)
		{
			if (InPlatform == BuildTargetPlatform.Win64)
			{
				return "x64";
			}
			return InPlatform.ToString();
		}

		public override bool RequiresVSUserFileGeneration()
		{
			return true;
		}
	}
}
