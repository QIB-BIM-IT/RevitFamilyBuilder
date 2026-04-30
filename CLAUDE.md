# Revit Family Builder — Claude Code instructions

This file is read first by Claude Code at the start of every session.

Follow these rules without exception.

-----

## What this project is

A C# Revit tool loaded through RF Tools Box that generates Revit families

from manufacturer datasheets. AI handles datasheet comprehension; a

deterministic engine handles all Revit API execution. Never mix these.

-----

## Entry point — DO NOT RENAME

- Namespace: `RevitFamilyBuilder`

- Class: `RevitFamilyBuilder`

- Method: `Execute(string message, UIApplication uiApp)`

This is required by the RF Tools Box loader. Renaming any of these

breaks the entire integration.

-----

## Working rules (non-negotiable)

1. **One PR = one problem = one test.** No exceptions. A chain of five

   patch-PRs trying to fix one issue once cost us the whole feature

   branch and forced a revert. Stick to scope or stop and ask.

1. **Never run a build.** Dave builds the DLL himself in Visual Studio.

   You can run `dotnet build` to check compilation if you want, but do

   not build the Revit target, and do not copy artifacts into the Revit

   addins folder.

1. **No direct commits to `main`.** Always work on a feature branch.

   Always open a PR. Dave reviews and merges himself.

1. **Diagnose before coding.** When given a task, first describe:

- which files you plan to modify and why

- rollback / backwards-compatibility considerations

- risks you see

  Wait for explicit validation before writing code.

1. **List what is out of scope.** Every PR description must explicitly

   confirm which areas were NOT touched.

1. **AI output must always be structured JSON only.** Never generate

   executable code from AI output. Never let AI improvise Revit API

   calls.

-----

## Architecture (what lives where)

The project has four strictly separated layers:

|Layer      |Responsibility                            |Where it lives                   |

|-----------|------------------------------------------|---------------------------------|

|AI         |Datasheet comprehension, intent extraction|Anthropic API calls, service code|

|Review UI  |Human-in-the-loop confirmation            |WPF window                       |

|JSON schema|Strict contract between AI and engine     |Schema + validator               |

|Engine     |All Revit API execution                   |Deterministic C# engine          |

Keep Revit API code separate from AI/service code. Never call Revit API

from the AI service layer, and never call the Anthropic API from the

engine layer.

-----

## Current engine capabilities (already implemented — do not reimplement)

The engine already does all of the following. Before adding anything,

check that it doesn’t already exist:

- Create family from `.rft` template

- Apply category

- Create parameters (instance / type)

- Create reference planes

- Create dimensions

- Create symbolic lines

- Create one rectangular extrusion

- Create multiple types

- Apply distinct values per type

- Apply formulas

- Export CSV

- Create / use a lookup table

- Create a round connector centered on a face

- Create a frontal rectangular void

- FlexTest

-----

## Stable baseline

The current stable commit is `13555f2`. The sample JSON baseline at this

commit covers: one extrusion, one void, one connector, two types, one

formula, FlexTest passing.

If a change breaks the baseline, stop. Do not patch on top — revert and

rediagnose.

-----

## Connector creation rules (empirical invariants — DO NOT VIOLATE)

These are empirical invariants of the Revit MEP connector API. Breaking

them causes silent connector sizing failures and “parameter not

accessible” warnings.

1. **Filter `Extrusion.IsSolid` before selecting a host face.** Voids

   are also extrusions and will happily be returned by face-normal

   matching. A connector hosted on a void produces a broken family.

1. **Regenerate after creating the connector.** After

   `ConnectorElement.CreateDuctConnector` (or the equivalent

   `CreateRoundConnector` / `CreateRectangularConnector` factories),

   call `doc.Regenerate()` BEFORE reading the connector’s built-in

   size parameters (`CONNECTOR_RADIUS`, `CONNECTOR_WIDTH`,

   `CONNECTOR_HEIGHT`). Without the regenerate, those parameters can

   come back null or read-only.

1. **Regenerate after associating size parameters.** After

   `FamilyManager.AssociateElementParameterToFamilyParameter` on a

   connector size parameter, call `doc.Regenerate()` AGAIN so that

   Revit propagates the family-parameter VALUE to the connector. The

   association mark (the small “=”) can appear without this second

   regenerate, but the connector will keep its default size until the

   document is regenerated.

-----

## Code style

- Keep implementations minimal and incremental.

- Prefer placeholder classes over overengineering.

- Match the existing patterns in the file you are editing.

- Do not refactor unrelated code, even if it looks improvable.

-----

## When in doubt, stop and ask

If a task seems to require touching anything outside its stated scope,

stop and surface that to Dave. Do not silently expand the PR. Do not

assume the scope was wrong — assume your understanding is incomplete.
 