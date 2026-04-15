namespace RevitFamilyBuilder.Schema
{
    public class ParameterDefinition
    {
        public string Name { get; set; }
        public ParameterType Type { get; set; }
        public string Group { get; set; }
        public bool IsInstance { get; set; }
        public string DefaultValue { get; set; }
    }
}
