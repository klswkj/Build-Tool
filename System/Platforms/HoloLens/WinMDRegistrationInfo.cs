using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
	// WinMD registration helper
	public class WinMDRegistrationInfo
	{
		// WinMD type info
		public class ActivatableType
		{
			// WinMD type info constructor
			public ActivatableType(string InTypeName, string InThreadingModelName)
			{
				TypeName = InTypeName;
				ThreadingModelName = InThreadingModelName;
			}

			public string TypeName { get; private set; }

			public string ThreadingModelName { get; private set; }
		}


		// Path to the WinRT library
		public string PackageRelativeDllPath { get; private set; }

		// List of the types in the WinMD
		public IEnumerable<ActivatableType> ActivatableTypes => ActivatableTypesList;

		private static readonly List<string> ResolveSearchPaths = new List<string>();
		private readonly List<ActivatableType> ActivatableTypesList;

		static WinMDRegistrationInfo()
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (Sender, EventArgs) => Assembly.ReflectionOnlyLoad(EventArgs.Name);
				WindowsRuntimeMetadata.ReflectionOnlyNamespaceResolve += (Sender, EventArgs) =>
				{
					string Path = WindowsRuntimeMetadata.ResolveNamespace(EventArgs.NamespaceName, ResolveSearchPaths).FirstOrDefault();
					if (Path == null)
					{
						return;
					}
					EventArgs.ResolvedAssemblies.Add(Assembly.ReflectionOnlyLoadFrom(Path));
				};

			}
		}

		// WinMD reference info
		public WinMDRegistrationInfo(FileReference InWindMDSourcePath, string InPackageRelativeDllPath)
		{
			PackageRelativeDllPath = InPackageRelativeDllPath;
			ResolveSearchPaths.Add(InWindMDSourcePath.Directory.FullName);

			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64)
			{
				ActivatableTypesList = new List<ActivatableType>();

				Assembly DependsOn = Assembly.ReflectionOnlyLoadFrom(InWindMDSourcePath.FullName);
				foreach (Type WinMDType in DependsOn.GetExportedTypes())
				{
					bool IsActivatable = false;
					string ThreadingModelName = "both";
					foreach (CustomAttributeData Attr in WinMDType.CustomAttributes)
					{
						if (Attr.AttributeType.FullName == "Windows.Foundation.Metadata.ActivatableAttribute" || Attr.AttributeType.FullName == "Windows.Foundation.Metadata.StaticAttribute")
						{
							IsActivatable = true;
						}
						else if (Attr.AttributeType.FullName == "Windows.Foundation.Metadata.ThreadingAttribute")
						{
							CustomAttributeTypedArgument Argument = Attr.ConstructorArguments[0];
							ThreadingModelName = Enum.GetName(Argument.ArgumentType, Argument.Value).ToLowerInvariant();
						}
					}
					if (IsActivatable)
					{
						ActivatableTypesList.Add(new ActivatableType(WinMDType.FullName, ThreadingModelName));
					}
				}
			}
		}
	} // End public class WinMDRegistrationInfo

}
