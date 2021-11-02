namespace BuildTool
{
#pragma warning disable IDE0052 // Remove unread private members
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable IDE0060 // Remove unused parameter

#if !ANDROID
	public partial class AndroidTargetRules
    {
	}

	public partial class ReadOnlyAndroidTargetRules
	{
		// The private mutable settings object
		protected readonly AndroidTargetRules Inner;

		public ReadOnlyAndroidTargetRules(AndroidTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !IOS
	public partial class IOSTargetRules
	{
	}

	public partial class ReadOnlyIOSTargetRules
	{
		// The private mutable settings object
		protected readonly IOSTargetRules Inner;

		public ReadOnlyIOSTargetRules(IOSTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !LUMIN
	public partial class LuminTargetRules
	{
	}

	public partial class ReadOnlyLuminTargetRules
	{
		// The private mutable settings object
		protected readonly LuminTargetRules Inner;

		public ReadOnlyLuminTargetRules(LuminTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !LINUX
	public partial class LinuxTargetRules
	{
	}

	public partial class ReadOnlyLinuxTargetRules
	{
		// The private mutable settings object
		protected readonly LinuxTargetRules Inner;

		public ReadOnlyLinuxTargetRules(LinuxTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !MAC
	public partial class MacTargetRules
	{
	}

	public partial class ReadOnlyMacTargetRules
	{
		// The private mutable settings object
		protected readonly MacTargetRules Inner;

		public ReadOnlyMacTargetRules(MacTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif // end MAC

#if !HOLOLENS
	public partial class HoloLensTargetRules
	{
        public HoloLensTargetRules(TargetInfo InTargetInfo)
        {
        }

	}

	public partial class ReadOnlyHoloLensTargetRules
	{
		// The private mutable settings object
		protected readonly HoloLensTargetRules Inner;

		public ReadOnlyHoloLensTargetRules(HoloLensTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

	// It's just Template
	/*
	public partial class XXXTargetRules
	{
	}

	public partial class ReadOnlyXXXTargetRules
	{
		// The private mutable settings object
		protected readonly XXXTargetRules Inner;

		public ReadOnlyXXXTargetRules(XXXTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
	*/

#if !XBOXONE
	public partial class XboxOneTargetRules
	{
	}
	
	public partial class ReadOnlyXboxOneTargetRules
	{
		// The private mutable settings object
		protected readonly XboxOneTargetRules Inner;

		public ReadOnlyXboxOneTargetRules(XboxOneTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !PS4
	public partial class PS4TargetRules
	{
	}
	
	public partial class ReadOnlyPS4TargetRules
	{
        // The private mutable settings object
        private readonly PS4TargetRules Inner;

        public ReadOnlyPS4TargetRules(PS4TargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif

#if !SWITCH
	public partial class SwitchTargetRules
	{
	}
	
	public partial class ReadOnlySwitchTargetRules
	{
		private readonly SwitchTargetRules Inner;

		public ReadOnlySwitchTargetRules(SwitchTargetRules Inner)
		{
			this.Inner = Inner;
		}
	}
#endif
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore IDE0079 // Remove unnecessary suppression
#pragma warning restore IDE0052 // Remove unread private members
}
