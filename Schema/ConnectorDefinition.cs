namespace RevitFamilyBuilder.Schema
{
    public class ConnectorDefinition
    {
        public string Name { get; set; }
        public string Domain { get; set; }
        public string Shape { get; set; }

        // Named face of the extrusion solid to host this connector.
        // v1 valid values: Front, Back, Left, Right, Top, Bottom
        public string Face { get; set; }

        // v1: must be "Center"
        public string Location { get; set; }

        public string DiameterParameter { get; set; }
    }
}
