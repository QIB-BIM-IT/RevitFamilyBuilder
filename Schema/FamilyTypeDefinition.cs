using System.Collections.Generic;

namespace RevitFamilyBuilder.Schema
{
    public class FamilyTypeDefinition
    {
        public string Name { get; set; }
        public Dictionary<string, string> ParameterValues { get; set; }

        public FamilyTypeDefinition()
        {
            ParameterValues = new Dictionary<string, string>();
        }
    }
}
