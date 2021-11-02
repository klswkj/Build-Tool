namespace BuildTool
{
    /*
    public partial class XXXToolChain
    {
    }
    */
#if !HOLOLENS
    public partial class HoloLensToolChain
    {
    }
#endif

#if !MAC
    public partial class MacToolChain
    {

    }
#endif

#if !LINUX
    public partial class LinuxToolChain
    {
    }
#endif

#if !WINDOWS
    public partial class VCToolChain
    {
    }
#endif
}
