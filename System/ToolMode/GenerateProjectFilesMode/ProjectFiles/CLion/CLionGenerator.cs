// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	
	// CLion project file generator which is just the CMakefileGenerator and only here for UBT to match against
	
	class CLionGenerator : CMakefileGenerator
	{
		
		// Creates a new instance of the <see cref="CMakefileGenerator"/> class.
		
		public CLionGenerator(FileReference InOnlyGameProject)
			: base(InOnlyGameProject)
		{
		}
	}
}
