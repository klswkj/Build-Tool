using System;

namespace BuildTool
{
	// Attribute which can be applied to a TargetRules-dervied class to indicate which configurations it supports
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
	public class SupportedConfigurationsAttribute : Attribute
	{
		public readonly TargetConfiguration[] Configurations;

		// Initialize the attribute with a list of configurations
		public SupportedConfigurationsAttribute(params TargetConfiguration[] InConfigurations)
		{
			Configurations = InConfigurations;
		}
	}
}
