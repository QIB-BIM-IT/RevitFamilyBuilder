# Revit Family Builder

This project is a C# Revit tool loaded through RF Tools Box.

## Entry point
- Namespace: RevitFamilyBuilder
- Class: RevitFamilyBuilder
- Method: Execute(string message, UIApplication uiApp)

## Product goal
Build a Revit family generator fully inside Revit.

## v1 scope
- Prompt entered in Revit
- Optional datasheet image path
- AI returns strict JSON only
- JSON is validated before any Revit action
- Family is generated in a family document first
- v1 supports only:
  - Generic Model family
  - parameters
  - reference planes
  - symbolic lines
  - simple dimensions
  - one simple extrusion

## Important constraints
- Do not assume a .addin manifest workflow
- Do not rename the RF Tools entry class or method
- Keep Revit API code separate from AI/service code
- Never generate executable code from AI output
- AI output must be structured JSON only
- Keep implementations minimal and incremental
- Prefer placeholder classes over overengineering

## Connector creation rules
These rules are empirical invariants of the Revit MEP connector API —
break them and connector sizing silently fails or emits
"parameter not accessible" warnings.

- Filter `Extrusion.IsSolid` before selecting a host face. Voids are
  also extrusions and will be happily returned by face-normal matching,
  but a connector hosted on a void produces a broken family.
- After `ConnectorElement.CreateDuctConnector` (or the equivalent
  `CreateRoundConnector` / `CreateRectangularConnector` factories),
  call `doc.Regenerate()` BEFORE reading the connector's built-in
  size parameters (`CONNECTOR_RADIUS`, `CONNECTOR_WIDTH`,
  `CONNECTOR_HEIGHT`). Without the regenerate those parameters can
  come back null or read-only.
- After `FamilyManager.AssociateElementParameterToFamilyParameter`
  on a connector size parameter, call `doc.Regenerate()` AGAIN so
  that Revit propagates the family-parameter VALUE to the connector.
  The association mark (little "=") can appear without this second
  regenerate, but the connector will keep its default size until the
  document is regenerated.