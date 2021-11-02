using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	class LinuxProjectGenerator : PlatformProjectGenerator
	{
		public LinuxProjectGenerator(CommandLineArguments Arguments)
			: base(Arguments)
		{
		}

		public override IEnumerable<BuildTargetPlatform> GetPlatforms()
		{
			yield return BuildTargetPlatform.Linux;
			yield return BuildTargetPlatform.LinuxAArch64;
		}

		public override bool HasVisualStudioSupport(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, VCProjectFileFormat ProjectFileFormat)
		{
			return false;
		}
	}
}
