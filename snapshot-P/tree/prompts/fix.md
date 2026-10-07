You are implementing code for a browser application built around a WASM semantic kernel.

Your job is not to create another frontend framework, SPA architecture, or JavaScript application that happens to call WASM.

The architectural goal is:

The browser renders and interacts. The WASM kernel owns application meaning.

Before writing code, inspect the repository, existing specifications, architecture documents, tests, and current implementation. Preserve existing decisions unless there is strong evidence they should change. If you find contradictions, document them rather than silently choosing one interpretation.

Core Architecture

Treat the system as these layers:

HTML
    document structure
    semantic markup
    forms
    native controls
    accessibility
    browser-native behavior
CSS
    presentation
    layout
    responsive behavior
    visual state
Minimal TypeScript / JavaScript
    DOM-to-WASM interop
    browser APIs unavailable directly to WASM
    event forwarding
    rendering/projection plumbing
    effect adapters
WASM Kernel
    authoritative application state
    domain rules
    legal transitions
    invariants
    validation
    capabilities
    obligations
    workflow
    derived state
    effect intentions
    application-level routing decisions

The WASM kernel is the semantic authority.

Do not allow application meaning to migrate into TypeScript merely because it is easier.

State-System-First Rule

Before implementing a feature, identify:

1. What state exists?
2. Which part of that state is authoritative?
3. What legal transitions exist?
4. What conditions guard those transitions?
5. What invariants must always remain true?
6. What evidence is required for a transition?
7. What capabilities are available in each state?
8. What unresolved obligations can exist?
9. What external effects can occur?
10. What happens if an external effect succeeds, fails, or has an unknown outcome?

Prefer explicit states and transitions over mutable flags and incidental behavior.

Do not represent important domain state using combinations of booleans when an explicit state model would better express the meaning.

For example, avoid:

isApproved
isPending
hasError
isSubmitting

when the domain is actually something like:

Draft
PendingReview
Approved
Rejected
SubmissionPending
SubmissionOutcomeUnknown

Illegal states should be difficult or impossible to represent.

Command, Transition, Projection

Use this conceptual flow:

user interaction
    ↓
browser event
    ↓
intent / command
    ↓
WASM kernel
    ↓
transition validation
    ↓
authoritative new state
    ↓
projection
    ↓
HTML update

A DOM event represents intent.

It does not directly mutate application state.

The kernel decides whether the requested transition is legal.

No Traditional Two-Way Binding

Do not create arbitrary two-way synchronization between DOM elements and mutable JavaScript objects.

Avoid architectures such as:

DOM
 ↕
JavaScript model
 ↕
WASM state

Prefer:

DOM input
    ↓
command
    ↓
kernel
    ↓
validated state
    ↓
projection
    ↓
DOM

Temporary browser-local interaction state is acceptable when it is truly presentation-only.

Examples might include:

* whether a tooltip is visible
* current pointer position
* animation progress
* transient focus information

Do not use that exception to move domain state into JavaScript.

HTML Wins

Whenever the browser or HTML already provides a capability adequately, use it.

Do not recreate native behavior in the kernel or TypeScript without a concrete reason.

Prefer native:

* links
* buttons
* forms
* inputs
* validation attributes where appropriate
* dialogs where appropriate
* semantic HTML
* browser history
* accessibility primitives
* CSS state and selectors
* browser events

The project should reduce overlap with the browser, not create abstractions over features the platform already possesses.

TypeScript Must Remain Thin

TypeScript should primarily be an adapter.

Every meaningful TypeScript function should be defensible as one of:

* DOM adapter
* WASM adapter
* browser API adapter
* effect adapter
* rendering/projection mechanism

If you start writing significant business logic in TypeScript, stop and reconsider the boundary.

Do not create:

* JavaScript domain services
* frontend business-rule engines
* duplicated validation logic
* frontend authorization rules
* large client state stores
* Redux-like state architectures
* component state machines duplicating kernel state
* arbitrary callback networks

without demonstrating why the kernel cannot appropriately own the behavior.

No Framework by Default

Do not introduce React, Vue, Angular, Svelte, Knockout, state-management libraries, routing frameworks, validation libraries, utility frameworks, or other dependencies unless they solve a demonstrated problem that cannot reasonably be solved with the platform and existing architecture.

A dependency is an architectural decision, not a convenience.

Before adding one, document:

* the exact problem
* why native capabilities are insufficient
* why a small local implementation is worse
* maintenance implications
* AI reasoning/context implications
* security/supply-chain implications

Default answer: no dependency.

Rendering

The kernel should expose projections suitable for rendering rather than leaking its entire internal state representation into the DOM.

Prefer deliberate projections such as:

PatientSummaryView
AvailableActions
NavigationState
SearchResults
ValidationMessages

over blindly serializing the entire application state tree.

Do not send the whole state across the WASM boundary merely because it is convenient.

Design projections around what the current view actually requires.

This becomes increasingly important as state grows.

Capabilities Over Permission Checks

Where practical, expose what the user/system can currently do, rather than forcing every UI element to independently reconstruct permission logic.

Prefer:

availableActions:
    Approve
    Reject
    RequestMoreInformation

over:

if role == X &&
   status == Y &&
   flagA &&
   !flagB &&
   ...

in frontend code.

The kernel determines capabilities.

The UI renders them.

Effects

External effects must be explicit.

Examples:

* HTTP requests
* persistence
* file access
* authentication
* browser storage
* navigation requiring external interaction
* notifications

Separate:

decision to perform effect

from:

execution of effect

and from:

observed result of effect

Do not assume that failure to receive confirmation means an effect did not occur.

Where relevant, support:

NotStarted
Pending
Succeeded
Failed
OutcomeUnknown

The kernel should remain able to reason about uncertainty.

Routing

Do not automatically assume a SPA router is required.

Use browser-native navigation/history where it provides the needed behavior.

Where application state determines what screen is legal or appropriate, the kernel may determine navigation intent while the browser performs navigation.

Keep URL/browser concerns separate from domain meaning.

WASM Boundary

Treat the WASM boundary as an API contract.

Minimize:

* crossings
* large object serialization
* unnecessary copying
* chatty calls
* leaking internal state representations

Prefer coarse, meaningful operations such as:

dispatch(command)
getProjection(view)
getCapabilities()
resolveEffect(result)

rather than hundreds of low-level getters and setters.

Do not optimize prematurely, but keep boundary costs visible.

Language Independence

Do not couple the browser architecture to a specific WASM implementation language.

The semantic kernel may ultimately be implemented in:

* F#
* C#
* Rust
* Kotlin/Java where viable
* another suitable strongly typed language

Keep the browser/kernel protocol sufficiently explicit that the kernel implementation can be replaced without redesigning the browser application.

Do not expose language-specific constructs across the public boundary unnecessarily.

AI Maintainability

This system is intentionally being designed to be understandable and maintainable by AI agents as well as humans.

Optimize for semantic clarity.

Prefer:

* explicit state names
* explicit transition names
* small modules
* deterministic behavior
* exhaustive handling
* explicit contracts
* clear invariants
* clear errors
* traceable decisions
* stable boundaries

Avoid cleverness, magic, convention-heavy behavior, implicit mutation, hidden lifecycle behavior, runtime monkey-patching, reflection-heavy design, and unnecessary metaprogramming.

The goal is to make the legal behavior of the application discoverable without requiring an agent to infer intent from thousands of lines of implementation.

Before Writing Code

For every meaningful feature, first produce a short implementation analysis containing:

Feature:
Authoritative state:
Relevant states:
Legal transitions:
Invariants:
Capabilities:
Inputs:
Outputs/projections:
External effects:
Browser responsibilities:
Kernel responsibilities:
TypeScript responsibilities:
Uncertainties:

If any important requirement is ambiguous, search existing repository evidence first.

Do not silently invent business rules.

When a reasonable implementation assumption must be made, mark it explicitly.

During Implementation

Continuously ask:

Am I putting this behavior in the layer that actually owns its meaning?

And:

Am I creating a second source of truth?

And:

Can an AI agent later determine why this behavior exists from the architecture itself, or would it have to infer intent from surviving code?

If the answer indicates semantic duplication or hidden intent, redesign before continuing.

Testing

Test the kernel primarily in terms of states and transitions.

Important tests should demonstrate:

given state X
and evidence/conditions Y
when command Z is requested
transition to state A is legal
and produces projection/effect B

Also test illegal transitions explicitly.

Prefer testing invariants over implementation details.

UI tests should verify that the browser correctly projects kernel state and sends the correct intents, rather than retesting domain logic already enforced by the kernel.

Definition of Done

A feature is not complete merely because the UI appears to work.

It is complete when:

* authoritative state is identified
* legal transitions are explicit
* invariants are enforced
* illegal transitions are rejected
* required capabilities are explicit
* effect outcomes are represented correctly
* browser and kernel responsibilities remain separated
* TypeScript contains no unnecessary domain semantics
* the WASM boundary remains coherent
* tests establish the important state behavior
* no unnecessary dependency was introduced
* assumptions and unresolved questions are documented
* another agent can understand the feature without reconstructing its meaning from incidental code

Most Important Constraint

Do not optimize for writing the least code today.

Optimize for reducing the amount of semantic reconstruction required tomorrow.

The project is testing whether explicit state, constrained transitions, small action spaces, and a thin browser layer can make software substantially easier and cheaper for humans and AI agents to maintain.

Protect that experiment while implementing the system.