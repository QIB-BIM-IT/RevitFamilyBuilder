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