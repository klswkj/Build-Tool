using System;

namespace BuildTool
{
	// Represents a layer within the rules hierarchy. 
	// Any module is created within a certain scope, and may only reference modules in an equal or parent scope 
	// (eg. engine modules cannot reference project modules).
	internal sealed class RulesScope
	{
		public string     Name;   // Name of this scope
		public RulesScope Parent; // The parent scope

		// Constructor
		public RulesScope(string Name, RulesScope Parent)
		{
			this.Name = Name;
			this.Parent = Parent;
		}

		// Checks whether this scope contains another scope
		// <param name="Other">The other scope to check</param>
		// <returns>True if this scope contains the other scope</returns>
		public bool Contains(RulesScope Other)
		{
			for(RulesScope Scope = this; Scope != null; Scope = Scope.Parent)
			{
				if(Scope == Other)
				{
					return true;
				}
			}
			return false;
		}

		// Formats the hierarchy of scopes
		// <returns>String representing the hierarchy of scopes</returns>
		public string FormatHierarchy()
		{
			if(Parent == null)
			{
				return Name;
			}
			else
			{
				return String.Format("{0} -> {1}", Name, Parent.FormatHierarchy());
			}
		}
	}
}
