using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	class MacProjectGenerator : PlatformProjectGenerator
	{
		public MacProjectGenerator(CommandLineArguments Arguments)
			: base(Arguments)
		{
		}
		
		public override IEnumerable<BuildTargetPlatform> GetPlatforms()
		{
			yield return BuildTargetPlatform.Mac;
		}

		public override bool HasVisualStudioSupport(BuildTargetPlatform InPlatform, TargetConfiguration InConfiguration, VCProjectFileFormat ProjectFileFormat) 
			=> false;
	}
}
