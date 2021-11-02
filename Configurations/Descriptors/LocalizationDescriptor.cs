using System.Diagnostics;
using BuildToolUtilities;

namespace BuildTool
{
	[DebuggerDisplay("Name={Name}")]
	public class LocalizationTargetDescriptor
	{
		public enum LocalizationTargetDescriptorLoadingPolicy
		{
			Never,
			Always,
			Editor,
			Game,
			PropertyNames,
			ToolTips,
		};

		public readonly string Name; // Name of this target

		// When should the localization data associated with a target should be loaded?
		private readonly LocalizationTargetDescriptorLoadingPolicy LoadingPolicy;

		private LocalizationTargetDescriptor(string InName, LocalizationTargetDescriptorLoadingPolicy InLoadingPolicy)
		{
			Name          = InName;
			LoadingPolicy = InLoadingPolicy;
		}

		public static LocalizationTargetDescriptor FromJsonObject(JsonObject InObject)
		{
			return new LocalizationTargetDescriptor(InObject.GetStringField(nameof(LocalizationTargetDescriptor.Name)), InObject.GetEnumField<LocalizationTargetDescriptorLoadingPolicy>(nameof(LocalizationTargetDescriptor.LoadingPolicy)));
		}

		private void Write(JsonWriter Writer)
		{
			Writer.WriteObjectStart();
			Writer.WriteValue(nameof(Name), Name);
			Writer.WriteValue(nameof(LoadingPolicy), LoadingPolicy.ToString());
			Writer.WriteObjectEnd();
		}

		// Write an array of target descriptors
		public static void WriteArray(JsonWriter Writer, string ArrayName, LocalizationTargetDescriptor[] Targets)
		{
			if (Targets != null)
			{
				if (0 < Targets.Length)
				{
					Writer.WriteArrayStart(ArrayName);
					foreach (LocalizationTargetDescriptor Target in Targets)
					{
						Target.Write(Writer);
					}
					Writer.WriteArrayEnd();
				}
			}
			else
			{
				throw new BuildException("Targets Data is null.");
			}
		}
	}
}
