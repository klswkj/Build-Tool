using BuildToolUtilities;

namespace BuildTool
{
	// Generates documentation from reflection data
	[ToolMode("WriteDocumentation", ToolModeOptions.None)]
	internal sealed class WriteDocumentationMode : ToolMode
	{
		// Enum for the type of documentation to generate
		private enum DocumentationType
		{
			BuildConfiguration,
			ModuleRules,
			TargetRules,
		}

		// Type of documentation to generate
		[CommandLine(Required = true)]
		private readonly DocumentationType Type = DocumentationType.BuildConfiguration;

		// The HTML file to write to
		[CommandLine(Required = true)]
		private readonly FileReference OutputFile = null;

		// Entry point for this command
		public override int Execute(CommandLineArguments Arguments)
		{
			Arguments.ApplyTo(this);
			Arguments.CheckAllArgumentsUsed();

			switch(Type)
			{
				case DocumentationType.BuildConfiguration:
					XMLConfig.WriteDocumentation(OutputFile);
					break;
				case DocumentationType.ModuleRules:
					RulesDocumentation.WriteDocumentation(typeof(ModuleRules), OutputFile);
					break;
				case DocumentationType.TargetRules:
					RulesDocumentation.WriteDocumentation(typeof(TargetRules), OutputFile);
					break;
				default:
					throw new BuildException("Invalid documentation type: {0}", Type);
			}

			return 0;
		}
	}
}
