namespace RevitFamilyBuilder.Schema
{
    /// <summary>
    /// A single MEP connector anchored on one face of a specific extrusion.
    ///
    /// Each connector is independent — pairing (e.g. an In + Out fitting)
    /// is NOT expressed as a single entry here; it is modelled as two
    /// separate connector entries that share the same
    /// <see cref="TargetGeometryId"/> and the same sizing parameters
    /// (<see cref="DiameterParameter"/> for Round,
    ///  <see cref="WidthParameter"/> + <see cref="HeightParameter"/> for
    /// Rectangular).
    /// </summary>
    public class ConnectorDefinition
    {
        /// <summary>Human-readable identifier used in log messages only.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Must match a <c>geometry[i].id</c> declared in the JSON. The engine
        /// uses the internal id → ElementId map (populated by AddGeometry) to
        /// locate the exact extrusion on which the connector is hosted, so two
        /// connectors placed on different geometries never fight over a face.
        /// </summary>
        public string TargetGeometryId { get; set; }

        /// <summary>
        /// Named face of the target extrusion. Accepted values (case
        /// insensitive): <c>back</c>, <c>front</c>, <c>left</c>, <c>right</c>,
        /// <c>top</c>, <c>bottom</c>. The engine matches each candidate face
        /// by its outward normal, so the geometry's plane choice (e.g. sharing
        /// a <c>Mid_LR</c> plane) does not affect face selection.
        /// </summary>
        public string TargetFace { get; set; }

        /// <summary>
        /// Revit <c>FlowDirectionType</c> value. Accepted values (case
        /// insensitive): <c>in</c>, <c>out</c>, <c>bidirectional</c>.
        /// Applied through <c>RBS_DUCT_FLOW_DIRECTION_PARAM</c> because
        /// <c>ConnectorElement.Direction</c> is read-only.
        /// </summary>
        public string FlowDirection { get; set; }

        /// <summary>
        /// Revit <c>DuctSystemType</c> value. Accepted values (case
        /// insensitive): <c>Global</c>, <c>SupplyAir</c>, <c>ReturnAir</c>,
        /// <c>ExhaustAir</c>, <c>Fitting</c>. Use <c>Global</c> for neutral
        /// generic fittings; the system classification is passed directly to
        /// <c>ConnectorElement.CreateDuctConnector</c> at creation time.
        /// </summary>
        public string SystemClassification { get; set; }

        /// <summary>
        /// Connector profile. Accepted values (case insensitive):
        /// <list type="bullet">
        ///   <item>
        ///     <description><c>Round</c> — requires
        ///     <see cref="DiameterParameter"/>; width/height parameters must
        ///     be left empty.</description>
        ///   </item>
        ///   <item>
        ///     <description><c>Rectangular</c> — requires
        ///     <see cref="WidthParameter"/> AND <see cref="HeightParameter"/>;
        ///     diameter parameter must be left empty.</description>
        ///   </item>
        /// </list>
        /// </summary>
        public string Profile { get; set; }

        /// <summary>
        /// Name of the family parameter that drives the connector's diameter
        /// (required when <see cref="Profile"/> is <c>Round</c>, forbidden
        /// otherwise). Two connectors pointing at the same parameter flex
        /// together.
        /// </summary>
        public string DiameterParameter { get; set; }

        /// <summary>
        /// Name of the family parameter that drives the connector's width
        /// (required when <see cref="Profile"/> is <c>Rectangular</c>,
        /// forbidden otherwise). Typically the family's <c>Width</c>
        /// parameter, so the connector flexes with the host extrusion.
        /// </summary>
        public string WidthParameter { get; set; }

        /// <summary>
        /// Name of the family parameter that drives the connector's height
        /// (required when <see cref="Profile"/> is <c>Rectangular</c>,
        /// forbidden otherwise). Typically the family's <c>Height</c>
        /// parameter.
        /// </summary>
        public string HeightParameter { get; set; }
    }
}
