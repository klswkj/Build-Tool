using System;
using BuildToolUtilities;

namespace BuildTool
{
	// Systems that need to be configured to execute a tool mode
	[Flags]
	enum ToolModeOptions
	{
		None                        = 0x00, // Do not initialize anything=
		StartPrefetchingEngine      = 0x01, // Start prefetching metadata for the engine folder as early as possible
		XmlConfig                   = 0x02, // Initializes the XmlConfig system
		BuildPlatforms              = 0x04, // Registers build platforms
		BuildPlatformsHostOnly      = 0x08, // Registers build platforms
		BuildPlatformsForValidation = 0x10, // Registers build platforms for validation
		SingleInstance              = 0x20, // Only allow a single instance running in the branch at once
		ShowExecutionTime           = 0x30, // Print out the total time taken to execute
	}

	// Attribute used to specify options for a UBT mode.
	internal sealed class ToolModeAttribute : Attribute
	{
		public string          ToolModeName;
		public ToolModeOptions Options;

		public ToolModeAttribute(string InName, ToolModeOptions InOptions)
		{
			ToolModeName = InName;
			Options      = InOptions;
		}
	}

	// Base class for standalone UBT modes. 
	// Different modes can be invoked using the -Mode=[Name] argument on the command line, 
	// where [Name] is determined by the ToolModeAttribute on a ToolMode derived class. 
	// The log system will be initialized before calling the mode, but little else.
	internal abstract class ToolMode
	{
		// Entry point for this command.
		public abstract int Execute(CommandLineArguments Arguments);
	}
}
