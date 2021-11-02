using System;
using System.IO;
using System.Text;

// RequiredProgramMainCPPInclude.h
// ->
// #pragma once
// # include "CoreMinimal.h"
// # include "LaunchEngineLoop.h"
// // this is highly sketchy, but we need some stuff from launchengineloop.cpp
// # include "Runtime/Launch/Private/LaunchEngineLoop.cpp"

namespace BuildTool
{
	// Parses include directives from cpp source files, to make dedicated PCHs
	// Called by BuildMode.
	internal static class CppIncludeParser
	{
		// Copies include directives from one file to another,
		// until an unsafe directive (or non-#include line) is found.
		public static void CopyIncludeDirectives(StreamReader Reader, StringWriter Writer)
		{
			StringBuilder Token = new StringBuilder();
			while(TryReadToken(Reader, Token))
			{
				if(1 < Token.Length || Token[0] != '\n')
				{
					if(Token.Length != 1 || Token[0] != '#')
					{
						break;
					}
					if(!TryReadToken(Reader, Token))
					{
						break;
					}

					string Directive = Token.ToString();

					if(Directive == "pragma")
					{
						if(!TryReadToken(Reader, Token) || Token.ToString() != "once")
						{
							break;
						}
						if(!TryReadToken(Reader, Token) || Token.ToString() != "\n")
						{
							break;
						}
					}
					else if(Directive == "include")
					{
						if(!TryReadToken(Reader, Token) || Token[0] != '\"')
						{
							break;
						}

						string IncludeFile = Token.ToString();

						if(!IncludeFile.EndsWith(Tag.Ext.Header + "\"") && !IncludeFile.EndsWith(Tag.Ext.Header + ">"))
						{
							break;
						}
						if(IncludeFile.Equals("\"RequiredProgramMainCPPInclude.h\"", StringComparison.OrdinalIgnoreCase))
						{
							break;
						}
						if(!TryReadToken(Reader, Token) || Token.ToString() != "\n")
						{
							break;
						}

						Writer.WriteLine(Tag.CppContents.Include + "{0}", IncludeFile);
					}
					else
					{
						break;
					}
				}
			}
		}

		// Reads an individual token from the input stream
		private static bool TryReadToken(StreamReader Reader, StringBuilder Token)
		{
			Token.Clear();

			int NextChar;
			for(;;)
			{
				NextChar = Reader.Read();
				if(NextChar == -1)
				{
					return false;
				}
				if(NextChar != ' ' && NextChar != '\t' && NextChar != '\r')
				{
					if(NextChar != '/')
					{
						break;
					}
					else if(Reader.Peek() == '/')
					{
						Reader.Read();
						for(;;)
						{
							NextChar = Reader.Read();
							if(NextChar == -1)
							{
								return false;
							}
							if(NextChar == '\n')
							{
								break;
							}
						}
					}
					else if(Reader.Peek() == '*')
					{
						Reader.Read();
						for(;;)
						{
							NextChar = Reader.Read();
							if(NextChar == -1)
							{
								return false;
							}
							if(NextChar == '*' && Reader.Peek() == '/')
							{
								break;
							}
						}
						Reader.Read();
					}
					else
					{
						break;
					}
				}
			}

			Token.Append((char)NextChar);

			if(Char.IsLetterOrDigit((char)NextChar))
			{
				for(;;)
				{
					NextChar = Reader.Read();
					if(NextChar == -1 || !Char.IsLetterOrDigit((char)NextChar))
					{
						break;
					}
					Token.Append((char)NextChar);
				}
			}
			else if(NextChar == '\"' || NextChar == '<')
			{
				for(;;)
				{
					NextChar = Reader.Read();
					if(NextChar == -1)
					{
						break;
					}

					Token.Append((char)NextChar);
					if(NextChar == '\"' || NextChar == '>')
					{
						break;
					}
				}
			}

			return true;
		}
	}
}
