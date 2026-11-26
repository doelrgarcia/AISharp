using AISharp.Compiler;
using AISharp.LanguageServer;
using AISharp.Lowering;
using AISharp.Package;
using AISharp.Runtime;
using AISharp.Tests.Harness;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

const string SkipCliGateTestsVariable = "AISHARP_SKIP_CLI_GATE_TESTS";
const int ChildProcessTimeoutMilliseconds = 300_000;

var tests = new (string Name, Action Body)[]
{
    ("parses representative AI# document", ParsesRepresentativeDocument),
    ("reports missing required contract before function body", ReportsMissingContractBeforeFunctionBody),
    ("reports duplicate policy declarations", ReportsDuplicatePolicyDeclarations),
    ("reports duplicate policy fields", ReportsDuplicatePolicyFields),
    ("reports unsupported policy fields", ReportsUnsupportedPolicyFields),
    ("parses policy fields as named properties", ParsesPolicyFieldsAsNamedProperties),
    ("reports policy inference values that are not local first or opt-in remote", ReportsUnsupportedPolicyInferenceValue),
    ("reports unsupported policy classification values", ReportsUnsupportedPolicyClassificationValue),
    ("reports unsupported policy training values", ReportsUnsupportedPolicyTrainingValue),
    ("reports unsupported policy telemetry values", ReportsUnsupportedPolicyTelemetryValue),
    ("accepts local-first policy diagnostics", AcceptsLocalFirstPolicyDiagnostics),
    ("accepts remote opt-in policy diagnostics", AcceptsRemoteOptInPolicyDiagnostics),
    ("reports remote model calls without remote opt-in", ReportsRemoteModelCallsWithoutRemoteOptIn),
    ("reports remote opt-in without tainted external text", ReportsRemoteOptInWithoutTaintedExternalText),
    ("reports remote opt-in without separated capabilities", ReportsRemoteOptInWithoutSeparatedCapabilities),
    ("reports unsupported policy security posture values", ReportsUnsupportedPolicySecurityPostureValues),
    ("reports duplicate capability declarations", ReportsDuplicateCapabilityDeclarations),
    ("reports duplicate resource declarations", ReportsDuplicateResourceDeclarations),
    ("parses schema and tool declarations", ParsesSchemaAndToolDeclarations),
    ("parses tool declarations with multiple capabilities and timeout", ParsesToolDeclarationsWithMultipleCapabilitiesAndTimeout),
    ("reports duplicate schema declarations", ReportsDuplicateSchemaDeclarations),
    ("reports missing required tool policy fields", ReportsMissingRequiredToolPolicyFields),
    ("reports non-wasm tool sandbox", ReportsNonWasmToolSandbox),
    ("reports invalid tool sandbox syntax", ReportsInvalidToolSandboxSyntax),
    ("reports invalid tool capability syntax", ReportsInvalidToolCapabilitySyntax),
    ("reports duplicate function declarations", ReportsDuplicateFunctionDeclarations),
    ("reports duplicate union cases", ReportsDuplicateUnionCases),
    ("reports function parameters using undeclared resources", ReportsUndeclaredResourceParameter),
    ("reports function parameters using undeclared capabilities", ReportsUndeclaredCapabilityParameter),
    ("reports function effects that were not declared at document scope", ReportsUndeclaredFunctionEffect),
    ("reports unsupported observability declaration kinds", ReportsUnsupportedObservationKind),
    ("reports unterminated string with line and column", ReportsUnterminatedStringLocation),
    ("audits complete spec progress", AuditsCompleteSpecProgress),
    ("audits in-progress spec progress", AuditsInProgressSpecProgress),
    ("audits blocked spec progress", AuditsBlockedSpecProgress),
    ("audits prose-only spec progress as in-progress", AuditsProseOnlySpecProgressAsInProgress),
    ("ignores line comments", ParsesLineComments),
    ("parses schema fields", ParsesSchemaFields),
    ("reports undeclared tool capability", ReportsUndeclaredToolCapability),
    ("parses record type declarations", ParsesRecordTypeDeclarations),
    ("parses bracketed effects syntax", ParsesBracketedEffectsSyntax),
    ("reports undeclared effect in bracketed syntax", ReportsUndeclaredEffectInBracketedSyntax),
    ("parses first-class effect taxonomy in signatures", ParsesFirstClassEffectTaxonomyInSignatures),
    ("reports document-scope built-in effect declarations", ReportsDocumentScopeBuiltInEffectDeclarations),
    ("parses invariant contracts", ParsesInvariantContracts),
    ("accepts invariant as required function contract", AcceptsInvariantAsRequiredFunctionContract),
    ("represents contract check semantics consistently", RepresentsContractCheckSemanticsConsistently),
    ("reports empty contract expressions clearly", ReportsEmptyContractExpressionsClearly),
    ("contract checker requires boolean contract expressions", ContractCheckerRequiresBooleanContractExpressions),
    ("contract checker scopes result to postconditions", ContractCheckerScopesResultToPostconditions),
    ("contract checker reports unknown contract names", ContractCheckerReportsUnknownContractNames),
    ("contract checker records runtime and verification metadata", ContractCheckerRecordsRuntimeAndVerificationMetadata),
    ("parses module declaration", ParsesModuleDeclaration),
    ("parses use declarations", ParsesUseDeclarations),
    ("reports duplicate module declarations", ReportsDuplicateModuleDeclarations),
    ("reports duplicate use declarations", ReportsDuplicateUseDeclarations),
    ("parses ownership-qualified resource parameters", ParsesOwnershipQualifiedResourceParameters),
    ("parses capability-bearing file and socket resources", ParsesCapabilityBearingFileAndSocketResources),
    ("reports undeclared ownership-qualified resource parameters", ReportsUndeclaredOwnershipQualifiedResourceParameter),
    ("reports ownership qualifiers on non-resource parameters", ReportsOwnershipQualifiersOnNonResourceParameters),
    ("parses async function declarations", ParsesAsyncFunctionDeclarations),
    ("parses actor declarations with state and async handlers", ParsesActorDeclarationsWithStateAndAsyncHandlers),
    ("parses structured concurrency statements", ParsesStructuredConcurrencyStatements),
    ("parses typed expression-oriented function bodies", ParsesTypedExpressionOrientedFunctionBodies),
    ("parses schema-constrained model call chains", ParsesSchemaConstrainedModelCallChains),
    ("represents provenance runtime metadata", RepresentsProvenanceRuntimeMetadata),
    ("parses model output schema references", ParsesModelOutputSchemaReferences),
    ("reports model calls using undeclared output schemas", ReportsModelCallsUsingUndeclaredOutputSchemas),
    ("reports model calls missing model ids", ReportsModelCallsMissingModelIds),
    ("reports model calls missing output schemas", ReportsModelCallsMissingOutputSchemas),
    ("reports model calls missing run invocations", ReportsModelCallsMissingRunInvocations),
    ("ignores model words inside ordinary expressions", IgnoresModelWordsInsideOrdinaryExpressions),
    ("parses typed AST macro and deterministic incremental generator declarations", ParsesTypedAstMacroAndDeterministicIncrementalGeneratorDeclarations),
    ("documents compiler pipeline stage boundaries", DocumentsCompilerPipelineStageBoundaries),
    ("represents lowered Roslyn artifact metadata", RepresentsLoweredRoslynArtifactMetadata),
    ("documents Roslyn analyzer metadata hooks", DocumentsRoslynAnalyzerMetadataHooks),
    ("represents CI release integrity gates", RepresentsCiReleaseIntegrityGates),
    ("represents release artifact provenance SBOM and signing metadata", RepresentsReleaseArtifactProvenanceSbomAndSigningMetadata),
    ("language server publishes compiler diagnostics", LanguageServerPublishesCompilerDiagnostics),
    ("language server advertises compiler metadata capabilities", LanguageServerAdvertisesCompilerMetadataCapabilities),
    ("reports disallowed compile-time side effects in macro and generator bodies", ReportsDisallowedCompileTimeSideEffectsInMacroAndGeneratorBodies),
    ("CLI JSON emits policy fields", CliJsonEmitsPolicyFields),
    ("CLI JSON emits schema fields", CliJsonEmitsSchemaFields),
    ("CLI JSON emits async and structured concurrency syntax", CliJsonEmitsAsyncAndStructuredConcurrencySyntax),
    ("CLI JSON emits actor declarations", CliJsonEmitsActorDeclarations),
    ("CLI JSON emits typed function body statements", CliJsonEmitsTypedFunctionBodyStatements),
    ("CLI JSON emits model call chains", CliJsonEmitsModelCallChains),
    ("CLI JSON emits provenance metadata", CliJsonEmitsProvenanceMetadata),
    ("CLI JSON emits schema metadata for model outputs", CliJsonEmitsSchemaMetadataForModelOutputs),
    ("CLI JSON emits metaprogramming declarations", CliJsonEmitsMetaprogrammingDeclarations),
    ("CLI JSON emits consistent camel-case model", CliJsonEmitsConsistentCamelCaseModel),
    ("CLI JSON emits source-map fields with stable property casing", CliJsonEmitsSourceMapFieldsWithStablePropertyCasing),
    ("CLI JSON emits structured diagnostics", CliJsonEmitsStructuredDiagnostics),
    ("CLI JSON emits resolved symbol metadata", CliJsonEmitsResolvedSymbolMetadata),
    ("CLI JSON emits first-class function effects", CliJsonEmitsFirstClassFunctionEffects),
    ("CLI JSON emits effect check summary", CliJsonEmitsEffectCheckSummary),
    ("CLI JSON exits nonzero for effect diagnostics", CliJsonExitsNonzeroForEffectDiagnostics),
    ("CLI JSON emits contract verification hooks", CliJsonEmitsContractVerificationHooks),
    ("CLI JSON emits contract semantic metadata", CliJsonEmitsContractSemanticMetadata),
    ("CLI validate command emits result envelope", CliValidateCommandEmitsResultEnvelope),
    ("CLI validate command accepts single-source sample directory", CliValidateCommandAcceptsSingleSourceSampleDirectory),
    ("CLI lower command writes generated CSharp artifact", CliLowerCommandWritesGeneratedCSharpArtifact),
    ("CLI build command compiles simple sample", CliBuildCommandCompilesSimpleSample),
    ("CLI format command normalizes source file", CliFormatCommandNormalizesSourceFile),
    ("formatter formats representative constructs idempotently", FormatterFormatsRepresentativeConstructsIdempotently),
    ("formatter output parses successfully", FormatterOutputParsesSuccessfully),
    ("CLI format check reports unformatted files without writing", CliFormatCheckReportsUnformattedFilesWithoutWriting),
    ("CLI format stdout prints formatted output without writing", CliFormatStdoutPrintsFormattedOutputWithoutWriting),
    ("CLI audit command emits spec progress result", CliAuditCommandEmitsSpecProgressResult),
    ("builds symbol table for valid document", BuildsSymbolTableForValidDocument),
    ("semantic model detects duplicate declarations in document scope", SemanticModelDetectsDuplicateDeclarationsInDocumentScope),
    ("semantic model detects cross-kind duplicate declarations in document scope", SemanticModelDetectsCrossKindDuplicateDeclarationsInDocumentScope),
    ("semantic model preserves module and effect source locations", SemanticModelPreservesModuleAndEffectSourceLocations),
    ("semantic model detects duplicate effect declarations", SemanticModelDetectsDuplicateEffectDeclarations),
    ("semantic model resolves actor members in actor scope", SemanticModelResolvesActorMembersInActorScope),
    ("semantic model detects duplicate actor members", SemanticModelDetectsDuplicateActorMembers),
    ("name resolution resolves declared types and primitives", NameResolutionResolvesDeclaredTypesAndPrimitives),
    ("name resolution detects undeclared type names", NameResolutionDetectsUndeclaredTypeNames),
    ("name resolution allows unknown names when use imports present", NameResolutionAllowsUnknownNamesWhenUseImportsPresent),
    ("name resolution resolves schema field types", NameResolutionResolvesSchemaFieldTypes),
    ("name resolution resolves actor state types", NameResolutionResolvesActorStateTypes),
    ("name resolution resolves generic argument types", NameResolutionResolvesGenericArgumentTypes),
    ("name resolution collects resolved symbol references", NameResolutionCollectsResolvedSymbolReferences),
    ("name resolution collects resolved type references", NameResolutionCollectsResolvedTypeReferences),
    ("type checker passes getActiveOrders", TypeCheckerPassesGetActiveOrders),
    ("type checker detects return type mismatch", TypeCheckerDetectsReturnTypeMismatch),
    ("type checker detects let annotation mismatch", TypeCheckerDetectsLetAnnotationMismatch),
    ("type checker types model call output as schema", TypeCheckerTypesModelCallOutputAsSchema),
    ("resource lifetime allows owned transfer once", ResourceLifetimeAllowsOwnedTransferOnce),
    ("resource lifetime detects use after transfer", ResourceLifetimeDetectsUseAfterTransfer),
    ("resource lifetime rejects borrowed resource escape", ResourceLifetimeRejectsBorrowedResourceEscape),
    ("resource lifetime requires owned resource disposal or transfer", ResourceLifetimeRequiresOwnedResourceDisposalOrTransfer),
    ("resource lifetime accepts using disposal", ResourceLifetimeAcceptsUsingDisposal),
    ("resource lifetime keeps ordinary values ergonomic", ResourceLifetimeKeepsOrdinaryValuesErgonomic),
    ("type checker models generic carriers and function types", TypeCheckerModelsGenericCarriersAndFunctionTypes),
    ("type checker types simple function calls", TypeCheckerTypesSimpleFunctionCalls),
    ("type checker validates record field access", TypeCheckerValidatesRecordFieldAccess),
    ("HIR builder builds document for sample", HirBuilderBuildsDocumentForSample),
    ("HIR builder assigns stable IDs to declarations", HirBuilderAssignsStableIdsToDeclations),
    ("HIR builder retains source locations", HirBuilderRetainsSourceLocations),
    ("HIR builder exposes resolved semantic references", HirBuilderExposesResolvedSemanticReferences),
    ("HIR builder preserves model-call metadata", HirBuilderPreservesModelCallMetadata),
    ("HIR builder lowers function bodies to HIR statements", HirBuilderLowersFunctionBodiesToHirStatements),
    ("effect checker passes sample with network effects", EffectCheckerPassesSampleWithNetworkEffects),
    ("effect checker detects missing model effect", EffectCheckerDetectsMissingModelEffect),
    ("effect checker detects missing network effect", EffectCheckerDetectsMissingNetworkEffect),
    ("effect checker detects pure function calling effectful function", EffectCheckerDetectsPureFunctionCallingEffectfulFunction),
    ("effect checker detects missing actor handler effect", EffectCheckerDetectsMissingActorHandlerEffect),
    ("effect checker reports compliant pure function", EffectCheckerReportsCompliantPureFunction),
    ("match statement parses basic match expression", MatchStatementParsesBasicMatchExpression),
    ("match statement reports non-exhaustive match", MatchStatementReportsNonExhaustiveMatch),
    ("match statement accepts wildcard arm", MatchStatementAcceptsWildcardArm),
    ("match statement reports duplicate and unreachable arms", MatchStatementReportsDuplicateAndUnreachableArms),
    ("match statement type checks arm result types", MatchStatementTypeChecksArmResultTypes),
    ("CLI JSON emits match statement structure", CliJsonEmitsMatchStatementStructure),
    ("MIR builder builds typed MIR for sample", MirBuilderBuildsTypedMirForSample),
    ("MIR builder types all expressions", MirBuilderTypesAllExpressions),
    ("MIR builder includes effect obligations", MirBuilderIncludesEffectObligations),
    ("MIR builder includes contract check plans", MirBuilderIncludesContractCheckPlans),
    ("MIR builder includes tool model actor handler and test nodes", MirBuilderIncludesToolModelActorHandlerAndTestNodes),
    ("MIR builder uses explicit error type for unresolved expressions", MirBuilderUsesExplicitErrorTypeForUnresolvedExpressions),
    ("MIR builder rejects invalid programs before lowering", MirBuilderRejectsInvalidProgramsBeforeLowering),
    ("MIR builder preserves source map links", MirBuilderPreservesSourceMapLinks),
    ("Lowering generates C# for minimal function", LoweringGeneratesCSharpForMinimalFunction),
    ("Lowering output contains stable namespace and class", LoweringOutputContainsStableNamespaceAndClass),
    ("Lowering output contains function signature", LoweringOutputContainsFunctionSignature),
    ("Lowering output is deterministic", LoweringOutputIsDeterministic),
    ("Lowering handles multiple functions", LoweringHandlesMultipleFunctions),
    ("Lowering result exposes source map", LoweringResultExposesSourceMap),
    ("Lowering emits C# record type declaration", LoweringEmitsCSharpRecordTypeDeclaration),
    ("Lowering emits C# union type hierarchy", LoweringEmitsCSharpUnionTypeHierarchy),
    ("Lowering generated C# compiles", LoweringGeneratedCSharpCompiles),
    ("Lowering emits requires guard at function entry", LoweringEmitsRequiresGuardAtFunctionEntry),
    ("Lowering emits ensures guard before return", LoweringEmitsEnsuresGuardBeforeReturn),
    ("Lowering contract guards compile", LoweringContractGuardsCompile),
    ("Lowering emits contract helper calls with source locations", LoweringEmitsContractHelperCallsWithSourceLocations),
    ("Lowering supports debug-only contract checks", LoweringSupportsDebugOnlyContractChecks),
    ("Lowering emits invariant after return value is available", LoweringEmitsInvariantAfterReturnValueIsAvailable),
    ("Lowering generated contract checks pass and fail with stable exception", LoweringGeneratedContractChecksPassAndFailWithStableException),
    ("Lowering emits C# switch expression for match statement", LoweringEmitsCSharpSwitchExpressionForMatchStatement),
    ("Lowering emits wildcard arm in switch expression", LoweringEmitsWildcardArmInSwitchExpression),
    ("Lowering match statement C# compiles", LoweringMatchStatementCSharpCompiles),
    ("Lowering emits async Task return for async function", LoweringEmitsAsyncTaskReturnForAsyncFunction),
    ("Lowering emits async Task for async unit function", LoweringEmitsAsyncTaskForAsyncUnitFunction),
    ("Lowering emits actor class with state and handlers", LoweringEmitsActorClassWithStateAndHandlers),
    ("Lowering emits spawn and all as Task.WhenAll", LoweringEmitsSpawnAndAllAsTaskWhenAll),
    ("Lowering async actor C# compiles", LoweringAsyncActorCSharpCompiles),
    ("Runtime contracts pass for valid precondition", RuntimeContractsPassForValidPrecondition),
    ("Runtime contracts fail with stable exception for violated precondition", RuntimeContractsFailWithStableExceptionForViolatedPrecondition),
    ("Runtime contracts fail for violated postcondition", RuntimeContractsFailForViolatedPostcondition),
    ("Runtime effects allow permitted effect kinds", RuntimeEffectsAllowPermittedEffectKinds),
    ("Runtime effects reject unpermitted effect kinds", RuntimeEffectsRejectUnpermittedEffectKinds),
    ("Runtime effects pure scope disallows all effects", RuntimeEffectsPureScopeDisallowsAllEffects),
    ("Runtime effects push and dispose restores previous scope", RuntimeEffectsPushAndDisposeRestoresPreviousScope),
    ("Runtime effects require current rejects denied effect", RuntimeEffectsRequireCurrentRejectsDeniedEffect),
    ("Runtime effects kinds parse maps AI# names", RuntimeEffectsKindsParseMapsAISharpNames),
    ("Runtime effect metadata attribute records declared kinds", RuntimeEffectMetadataAttributeRecordsDeclaredKinds),
    ("Lowering emits effect metadata attribute for declared effects", LoweringEmitsEffectMetadataAttributeForDeclaredEffects),
    ("Lowering emits effect guard for model function", LoweringEmitsEffectGuardForModelFunction),
    ("Lowering emits inline AISharpEffects runtime support", LoweringEmitsInlineEffectRuntimeSupport),
    ("Lowering emits effect metadata None for pure function", LoweringEmitsEffectMetadataNoneForPureFunction),
    ("Lowering emits effect guard for actor handler", LoweringEmitsEffectGuardForActorHandler),
    ("Lowering generated effect guards compile and enforce scope", LoweringGeneratedEffectGuardsCompileAndEnforceScope),
    ("Runtime policy returns safe defaults for unknown policy", RuntimePolicyReturnsSafeDefaultsForUnknownPolicy),
    ("Runtime policy enforces local-only inference by default", RuntimePolicyEnforcesLocalOnlyInferenceByDefault),
    ("Runtime policy accepts remote inference with correct policy", RuntimePolicyAcceptsRemoteInferenceWithCorrectPolicy),
    ("Runtime policy rejects remote inference without opt-in", RuntimePolicyRejectsRemoteInferenceWithoutOptIn),
    ("Runtime provenance store appends and reads records", RuntimeProvenanceStoreAppendsAndReadsRecords),
    ("Runtime provenance store filters by kind", RuntimeProvenanceStoreFiltersByKind),
    ("Runtime tool sandbox denies undeclared capability", RuntimeToolSandboxDeniesUndeclaredCapability),
    ("Runtime tool sandbox invokes registered tool", RuntimeToolSandboxInvokesRegisteredTool),
    ("Runtime tool sandbox enforces timeout", RuntimeToolSandboxEnforcesTimeout),
    ("Runtime tool sandbox captures provenance with hashes", RuntimeToolSandboxCapturesProvenanceWithHashes),
    ("Runtime tool sandbox validates output schema", RuntimeToolSandboxValidatesOutputSchema),
    ("Runtime tool sandbox passes output with valid schema", RuntimeToolSandboxPassesOutputWithValidSchema),
    ("Runtime wasm boundary enforces capabilities", RuntimeWasmBoundaryEnforcesCapabilities),
    ("Runtime resource scope disposes resources on exit", RuntimeResourceScopeDisposesResourcesOnExit),
    ("Runtime actor context exposes identity and effect scope", RuntimeActorContextExposesIdentityAndEffectScope),
    ("Runtime model runtime executes fake schema-constrained call", RuntimeModelRuntimeExecutesFakeSchemaConstrainedCall),
    ("Runtime model runtime rejects missing schema", RuntimeModelRuntimeRejectsMissingSchema),
    ("Runtime model runtime rejects remote provider without policy", RuntimeModelRuntimeRejectsRemoteProviderWithoutPolicy),
    ("Runtime model runtime rejects remote provider with local-only policy", RuntimeModelRuntimeRejectsRemoteProviderWithLocalOnlyPolicy),
    ("Runtime model runtime accepts remote provider with opt-in policy", RuntimeModelRuntimeAcceptsRemoteProviderWithOptInPolicy),
    ("Runtime model runtime reports unregistered handler clearly", RuntimeModelRuntimeReportsUnregisteredHandlerClearly),
    ("Runtime model runtime health check returns true", RuntimeModelRuntimeHealthCheckReturnsTrue),
    ("Runtime schema validator passes valid payload", RuntimeSchemaValidatorPassesValidPayload),
    ("Runtime schema validator fails on missing required field", RuntimeSchemaValidatorFailsOnMissingRequiredField),
    ("Runtime schema validator fails on wrong type", RuntimeSchemaValidatorFailsOnWrongType),
    ("Runtime schema validator passes with extra fields in default mode", RuntimeSchemaValidatorPassesWithExtraFieldsInDefaultMode),
    ("Runtime schema validator rejects extra fields in strict mode", RuntimeSchemaValidatorRejectsExtraFieldsInStrictMode),
    ("Runtime schema validator fails on invalid JSON", RuntimeSchemaValidatorFailsOnInvalidJson),
    ("Runtime schema validator preserves schema and call-site locations", RuntimeSchemaValidatorPreservesSchemaAndCallSiteLocations),
    ("Runtime schema can be built from parsed AISharp schema fields", RuntimeSchemaCanBeBuiltFromParsedAISharpSchemaFields),
    ("Runtime schema registry validates by name", RuntimeSchemaRegistryValidatesByName),
    ("Runtime schema registry throws for unregistered schema", RuntimeSchemaRegistryThrowsForUnregisteredSchema),
    ("Runtime schema registry RequireValid throws on failure", RuntimeSchemaRegistryRequireValidThrowsOnFailure),
    ("Runtime file provenance store appends and reads records", RuntimeFileProvenanceStoreAppendsAndReadsRecords),
    ("Runtime file provenance store round-trips all record kinds", RuntimeFileProvenanceStoreRoundTripsAllRecordKinds),
    ("Runtime file provenance store reads by kind", RuntimeFileProvenanceStoreReadsByKind),
    ("Runtime provenance serializer round-trips without sensitive fields", RuntimeProvenanceSerializerRoundTripsWithoutSensitiveFields),
    ("CLI lower command emits provenance path in JSON output", CliLowerCommandEmitsProvenancePath),
    ("CLI lower command persists source and lowered artifact hashes", CliLowerCommandPersistsSourceAndLoweredArtifactHashes),
    ("AI patch proposal validator accepts valid proposal", AiPatchProposalValidatorAcceptsValidProposal),
    ("AI patch proposal validator rejects empty changed files", AiPatchProposalValidatorRejectsEmptyChangedFiles),
    ("AI patch proposal validator rejects missing rationale", AiPatchProposalValidatorRejectsMissingRationale),
    ("AI patch proposal helper returns valid proposal from fake model", AiPatchProposalHelperReturnsValidProposal),
    ("AI patch proposal helper rejects invalid schema output", AiPatchProposalHelperRejectsInvalidSchemaOutput),
    ("AI patch proposal captures provenance without mutating files", AiPatchProposalCapturesProvenanceWithoutMutatingFiles),
    ("AI patch proposal provenance round-trips through file store", AiPatchProposalProvenanceRoundTrips),
    ("Ollama runtime returns success response from fake handler", OllamaRuntimeReturnsSuccessFromFakeHandler),
    ("Ollama runtime returns failure when HTTP returns non-200", OllamaRuntimeReturnsFailureOnHttpError),
    ("Ollama runtime returns failure when server is unreachable", OllamaRuntimeReturnsFailureOnNetworkError),
    ("Ollama runtime health check passes with fake 200 server", OllamaRuntimeHealthCheckPassesWithFakeServer),
    ("Ollama runtime health check fails when server is unreachable", OllamaRuntimeHealthCheckFailsWhenUnreachable),
    ("Ollama runtime rejects request with missing schema name", OllamaRuntimeRejectsMissingSchemaName),
    ("Ollama runtime applies replay seed and temperature options", OllamaRuntimeAppliesReplayOptions),
    ("Ollama runtime sends expected generate request shape", OllamaRuntimeSendsExpectedGenerateRequestShape),
    ("Ollama runtime accepts local-only policy configuration", OllamaRuntimeAcceptsLocalOnlyPolicyConfiguration),
    ("Ollama runtime reports actionable unavailable diagnostics", OllamaRuntimeReportsActionableUnavailableDiagnostics),
    ("LSP service opens document and returns diagnostics", LspServiceOpensDocumentAndReturnsDiagnostics),
    ("LSP service returns no diagnostics for valid document", LspServiceReturnsNoDiagnosticsForValidDocument),
    ("LSP service change document updates snapshot and diagnostics", LspServiceChangeDocumentUpdatesSnapshot),
    ("LSP service close document removes it from store", LspServiceCloseDocumentRemovesFromStore),
    ("LSP service get diagnostics throws for unopened document", LspServiceGetDiagnosticsThrowsForUnopenedDocument),
    ("LSP service maintains version through open and change", LspServiceMaintainsVersionThroughChanges),
    ("LSP document store tracks multiple open documents", LspDocumentStoreTracksMultipleDocuments),
    ("LSP changing invalid to valid clears diagnostics", LspChangingInvalidToValidClearsDiagnostics),
    ("LSP changing valid to invalid publishes diagnostics", LspChangingValidToInvalidPublishesDiagnostics),
    ("LSP diagnostic locations are zero-based", LspDiagnosticLocationsAreZeroBased),
    ("LSP incremental change applies partial text replacement", LspIncrementalChangeAppliesPartialTextReplacement),
    ("LSP incremental change on service updates snapshot and returns new diagnostics", LspIncrementalChangeOnServiceUpdatesSnapshotAndReturnsNewDiagnostics),
    ("LSP get document symbols returns top-level declarations", LspGetDocumentSymbolsReturnsTopLevelDeclarations),
    ("LSP get document symbols includes kind", LspGetDocumentSymbolsIncludesKind),
    ("LSP get document symbols includes module and actor handlers", LspGetDocumentSymbolsIncludesModuleAndActorHandlers),
    ("LSP get hover returns symbol info", LspGetHoverReturnsSymbolInfo),
    ("LSP get hover for function shows signature effects and contracts", LspGetHoverForFunctionShowsSignatureEffectsAndContracts),
    ("LSP get completions includes keywords", LspGetCompletionsIncludesKeywords),
    ("LSP get completions includes document symbols", LspGetCompletionsIncludesDocumentSymbols),
    ("LSP get completions includes known effects and contract keywords", LspGetCompletionsIncludesEffectsAndContractKeywords),
    ("LSP get completions inside function body includes parameters", LspGetCompletionsInsideFunctionBodyIncludesParameters),
    ("LSP get document symbols via service for open document", LspGetDocumentSymbolsViaServiceForOpenDocument),
    ("Remote provider boundary rejected without policy name", RemoteProviderBoundaryRejectedWithoutPolicyName),
    ("Remote provider boundary rejected with local-only policy", RemoteProviderBoundaryRejectedWithLocalOnlyPolicy),
    ("Remote provider boundary rejected without remote opt-in", RemoteProviderBoundaryRejectedWithoutRemoteOptIn),
    ("Remote provider boundary succeeds with full opt-in policy", RemoteProviderBoundarySucceedsWithFullOptInPolicy),
    ("Remote provider boundary provenance includes provider kind without secrets", RemoteProviderBoundaryProvenanceIncludesProviderKind),
    ("Remote provider boundary health check returns true", RemoteProviderBoundaryHealthCheckReturnsTrue),
    ("Policy engine default is local-only deny-by-default", PolicyEngineDefaultIsLocalOnlyDenyByDefault),
    ("Policy engine remote opt-in is required for remote calls", PolicyEngineRemoteOptInRequiredForRemoteCalls),
    ("Policy engine honors runtime context decisions", PolicyEngineHonorsRuntimeContextDecisions),
    ("Policy engine local call is allowed by default", PolicyEngineLocalCallIsAllowedByDefault),
    ("Policy engine tool capabilities denied unless granted", PolicyEngineToolCapabilitiesDeniedUnlessGranted),
    ("Policy engine tool capabilities allowed with explicit grant", PolicyEngineToolCapabilitiesAllowedWithExplicitGrant),
    ("Policy engine unsafe interop forbidden by default", PolicyEngineUnsafeInteropForbiddenByDefault),
    ("Policy engine unsafe interop allowed when declared", PolicyEngineUnsafeInteropAllowedWhenDeclared),
    ("Policy engine provenance required for remote calls", PolicyEngineProvenanceRequiredForRemoteCalls),
    ("Policy engine diagnostics recorded for every denial", PolicyEngineDiagnosticsRecordedForEveryDenial),
    ("Policy engine throw if denied converts to exception", PolicyEngineThrowIfDeniedConvertsToException),
    ("Policy engine telemetry enabled emits warning", PolicyEngineTelemetryEnabledEmitsWarning),
    ("LSP go-to-definition resolves schema declaration", LspGoToDefinitionResolvesSchemaDeclaration),
    ("LSP go-to-definition resolves function declaration", LspGoToDefinitionResolvesFunctionDeclaration),
    ("LSP go-to-definition returns null for unknown word", LspGoToDefinitionReturnsNullForUnknownWord),
    ("LSP find references includes declaration and usages", LspFindReferencesIncludesDeclarationAndUsages),
    ("LSP find references via service returns uri", LspFindReferencesViaServiceReturnsUri),
    ("LSP go-to-definition via service resolves schema", LspGoToDefinitionViaServiceResolvesSchema),
    ("LSP service formats open document", LspServiceFormatsOpenDocument),
    ("Debug map lowering produces source map artifact", DebugMapLoweringProducesSourceMapArtifact),
    ("Debug map forward mapping resolves function body line", DebugMapForwardMappingResolvesFunctionBodyLine),
    ("Debug map reverse mapping resolves generated line to aisharp", DebugMapReverseMappingResolvesGeneratedLine),
    ("Debug map all entries have member names", DebugMapAllEntriesHaveMemberNames),
    ("Package manifest parses valid manifest", PackageManifestParsesValidManifest),
    ("Package manifest requires name field", PackageManifestRequiresNameField),
    ("Package manifest requires version field", PackageManifestRequiresVersionField),
    ("Package manifest requires package section", PackageManifestRequiresPackageSection),
    ("Package manifest rejects invalid package name", PackageManifestRejectsInvalidPackageName),
    ("Package manifest warns on duplicate keys", PackageManifestWarnsOnDuplicateKeys),
    ("Package manifest parses inline array sources", PackageManifestParsesInlineArraySources),
    ("Package manifest parses dependencies section", PackageManifestParsesDependenciesSection),
    ("Package manifest parses build section", PackageManifestParsesBuildSection),
    ("Package manifest parses policy section", PackageManifestParsesPolicySection),
    ("Package lockfile round-trips through serialize and deserialize", PackageLockfileRoundTrips),
    ("Package manifest locator finds nearest manifest", PackageManifestLocatorFindsNearestManifest),
    ("NuGet packages are produced for all library projects", NuGetPackagesProducedForAllLibraryProjects),
    ("Release hash manifest hashes artifact files", ReleaseHashManifestHashesArtifactFiles),
    ("Release hash manifest round-trips through JSON", ReleaseHashManifestRoundTrips),
    ("Release SBOM minimal contains all library components", ReleaseSbomMinimalContainsAllComponents),
    ("Release SBOM round-trips through JSON", ReleaseSbomRoundTrips),
    ("Release provenance record serializes gates", ReleaseProvenanceRecordSerializesGates),
    ("Release signing dry-run with no provider emits warning", ReleaseSigningDryRunWithNoProviderEmitsWarning),
    ("Release signing dry-run validates artifact existence", ReleaseSigningDryRunValidatesArtifactExistence),
    ("Release signing dry-run succeeds with existing artifacts", ReleaseSigningDryRunSucceedsWithExistingArtifacts),
    ("Signing configuration rejects secret-like fields", SigningConfigurationRejectsSecretLikeFields),
    ("Signing configuration loads external provider hint from JSON", SigningConfigurationLoadsExternalProviderHintFromJson),
    ("Signing configuration missing file returns null", SigningConfigurationMissingFileReturnsNull),
    ("Release provenance carries signature path and dry-run state", ReleaseProvenanceCarriesSignaturePathAndDryRunState),
    ("CLI sign dry-run emits warning when no configuration provided", CliSignDryRunEmitsWarningWhenNoConfigurationProvided),
    ("CLI sign dry-run validates artifact list", CliSignDryRunValidatesArtifactList),
    ("CLI sign reports clear diagnostic for missing configuration file", CliSignReportsClearDiagnosticForMissingConfigurationFile),
    ("CLI sign rejects real signing without dry-run", CliSignRejectsRealSigningWithoutDryRun),
    ("CLI release dry-run command emits gates and packages", CliReleaseDryRunEmitsGatesAndPackages),
    ("CLI acceptance command emits all gates", CliAcceptanceCommandEmitsAllGates),
    ("Build harness compiles generated C# from hello AI# sample", BuildHarnessCompilesGeneratedCSharpFromHelloSample),
    ("Build harness produces generated assembly artifact for sample", BuildHarnessProducesGeneratedAssemblyArtifactForSample),
    ("Build harness captures useful diagnostics on broken generated C#", BuildHarnessCapturesUsefulDiagnosticsOnBrokenGeneratedCSharp),
    ("Build harness preserves artifacts when debug environment variable is set", BuildHarnessPreservesArtifactsWhenDebugEnvironmentVariableIsSet),
    ("Service library sample lowers and builds end-to-end", ServiceLibrarySampleLowersAndBuildsEndToEnd),
};

var failed = 0;
var skipCliGateTests = string.Equals(
    Environment.GetEnvironmentVariable(SkipCliGateTestsVariable),
    "1",
    StringComparison.OrdinalIgnoreCase);
foreach (var test in tests)
{
    var filter = args.FirstOrDefault(a => a.StartsWith("--filter=", StringComparison.Ordinal))?["--filter=".Length..];
    if (filter is not null && !test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var exact = args.FirstOrDefault(a => a.StartsWith("--test=", StringComparison.Ordinal))?["--test=".Length..];
    if (exact is not null && !string.Equals(test.Name, exact, StringComparison.Ordinal)) continue;
    if (skipCliGateTests && IsCliGateTest(test.Name))
    {
        Console.WriteLine($"SKIP {test.Name}");
        continue;
    }

    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static bool IsCliGateTest(string testName) =>
    testName is "CLI release dry-run command emits gates and packages"
        or "CLI acceptance command emits all gates"
        or "CLI sign dry-run emits warning when no configuration provided"
        or "CLI sign dry-run validates artifact list"
        or "CLI sign reports clear diagnostic for missing configuration file"
        or "CLI sign rejects real signing without dry-run";

static void ParsesRepresentativeDocument()
{
    const string source = """
        intent "Fetch active orders for a customer and never return archived orders"
        policy CustomerPII {
            classification = secret
            inference = local_only
            external_text = tainted
            capabilities = separated
            training = forbidden
            telemetry = opt_in_aggregate
        }
        capability OrdersApi
        resource ModelSession
        effect net, json
        type OrderStatus = Active | Archived | Pending
        type OrderError = NetworkFailure | Unauthorized | InvalidPayload
        fn getActiveOrders(customerId: CustomerId, api: OrdersApi)
            -> Result<List<Order>, OrderError>
            effect(net)
            requires customerId != empty
            ensures result is Ok => result.value.all(o => o.status == Active)
            invariant result is Err || result.value.count >= 0
        {
            let response = await api.get("/customers/{customerId}/orders")
            let orders = decode<List<Order>>(response)?
            return Ok(orders.filter(o => o.status == Active))
        }
        test "archived orders are filtered" {
            expect getActiveOrders(CustomerId(42), api) == Ok(sampleActiveOrders())
        }
        observe {
            counter "orders.fetch.count"
            histogram "orders.fetch.duration.ms"
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    AssertTrue(result.Document.Intent == "Fetch active orders for a customer and never return archived orders", "Intent was not captured.");
    AssertTrue(result.Document.Policies.Single().Name == "CustomerPII", "Policy was not captured.");
    AssertTrue(result.Document.Capabilities.Single().Name == "OrdersApi", "Capability was not captured.");
    AssertTrue(result.Document.Resources.Single().Name == "ModelSession", "Resource was not captured.");
    AssertTrue(result.Document.Effects.SequenceEqual(["net", "json"]), "Effects were not captured.");
    AssertTrue(result.Document.Types.Count == 2, "Expected two union types.");
    AssertTrue(result.Document.Functions.Single().Name == "getActiveOrders", "Function name was not captured.");
    AssertTrue(result.Document.Functions.Single().Contracts.Count == 3, "Requires/ensures/invariant contracts were not captured.");
    AssertTrue(result.Document.Tests.Single().Name == "archived orders are filtered", "Inline test was not captured.");
    AssertTrue(result.Document.Observability.Count == 2, "Observability declarations were not captured.");
}

static void ReportsMissingContractBeforeFunctionBody()
{
    const string source = """
        intent "No contract"
        fn fetch()
            -> Result<Order, OrderError>
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI1006"), "Expected missing contract diagnostic AI1006.");
}

static void ReportsDuplicatePolicyDeclarations()
{
    const string source = """
        intent "Duplicate policy"
        policy CustomerPII {
            classification = secret
        }
        policy CustomerPII {
            classification = public
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2011"), "Expected duplicate policy diagnostic AI2011.");
}

static void ReportsDuplicatePolicyFields()
{
    const string source = """
        intent "Duplicate policy fields"
        policy CustomerPII {
            classification = secret
            classification = internal
            inference = local_only
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2020"), "Expected duplicate policy field diagnostic AI2020.");
}

static void ReportsUnsupportedPolicyFields()
{
    const string source = """
        intent "Unsupported policy field"
        policy CustomerPII {
            classification = secret
            retention = forever
            inference = local_only
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2021"), "Expected unsupported policy field diagnostic AI2021.");
}

static void ParsesPolicyFieldsAsNamedProperties()
{
    const string source = """
        intent "Named policy properties"
        policy CustomerPII {
            classification = secret
            inference = local_only
            external_text = tainted
            capabilities = separated
            training = forbidden
            telemetry = opt_in_aggregate
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var policy = result.Document.Policies.Single();
    AssertTrue(policy.Classification == "secret", "Policy classification property was not captured.");
    AssertTrue(policy.Inference == "local_only", "Policy inference property was not captured.");
    AssertTrue(policy.Training == "forbidden", "Policy training property was not captured.");
    AssertTrue(policy.Telemetry == "opt_in_aggregate", "Policy telemetry property was not captured.");
}

static void ReportsUnsupportedPolicyInferenceValue()
{
    const string source = """
        intent "Unsafe remote inference policy"
        policy CustomerPII {
            classification = secret
            inference = remote_allowed
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2022"), "Expected unsupported policy inference value diagnostic AI2022.");
}

static void ReportsUnsupportedPolicyClassificationValue()
{
    const string source = """
        intent "Unsupported classification policy"
        policy CustomerPII {
            classification = confidential
            inference = local_only
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2023"), "Expected unsupported policy classification value diagnostic AI2023.");
}

static void ReportsUnsupportedPolicyTrainingValue()
{
    const string source = """
        intent "Unsupported training policy"
        policy CustomerPII {
            classification = secret
            inference = local_only
            training = allowed
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2024"), "Expected unsupported policy training value diagnostic AI2024.");
}

static void ReportsUnsupportedPolicyTelemetryValue()
{
    const string source = """
        intent "Unsupported telemetry policy"
        policy CustomerPII {
            classification = secret
            inference = local_only
            telemetry = full_capture
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2025"), "Expected unsupported policy telemetry value diagnostic AI2025.");
}

static void AcceptsLocalFirstPolicyDiagnostics()
{
    const string source = """
        intent "Local-first policy diagnostics"
        policy SourceCode {
            classification = secret
            inference = local_only
            external_text = tainted
            capabilities = separated
            training = forbidden
            telemetry = forbidden
        }
        schema PatchPlan = {
            files: List<SourceFile>
        }
        fn propose_patch(request: ChangeRequest)
            -> Result<PatchPlan, Error>
            effects [model]
            requires request != empty
        {
            return model local "qwen3-coder"
                .task("Produce a patch plan")
                .input(request)
                .output<PatchPlan>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected local-only policy with tainted external text and separated capabilities to pass.");
    var policy = result.Document.Policies.Single();
    AssertTrue(policy.ExternalText == "tainted", "Policy external_text property was not captured.");
    AssertTrue(policy.Capabilities == "separated", "Policy capabilities property was not captured.");
}

static void AcceptsRemoteOptInPolicyDiagnostics()
{
    const string source = """
        intent "Remote opt-in policy diagnostics"
        policy PublicDocs {
            classification = public
            inference = remote_opt_in
            external_text = tainted
            capabilities = separated
            training = forbidden
            telemetry = opt_in_aggregate
        }
        schema Summary = {
            text: Text
        }
        fn summarize(doc: ExternalText)
            -> Result<Summary, Error>
            effects [model]
            requires doc != empty
        {
            return model openai "gpt-5.4-mini"
                .task("Summarize this document")
                .input(doc)
                .output<Summary>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected explicit remote opt-in with tainted external text and separated capabilities to pass.");
}

static void ReportsRemoteModelCallsWithoutRemoteOptIn()
{
    const string source = """
        intent "Reject remote model under local-only policy"
        policy SourceCode {
            classification = internal
            inference = local_only
            external_text = tainted
            capabilities = separated
        }
        schema PatchPlan = {
            files: List<SourceFile>
        }
        fn propose_patch(request: ChangeRequest)
            -> Result<PatchPlan, Error>
            effects [model]
            requires request != empty
        {
            return model openai "gpt-5.4-mini"
                .task("Produce a patch plan")
                .input(request)
                .output<PatchPlan>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2049");
    AssertTrue(diagnostic is not null, "Expected remote model policy diagnostic AI2049.");
    AssertTrue(diagnostic!.Message.Contains("remote_opt_in", StringComparison.Ordinal), "Expected diagnostic to name the required remote opt-in policy.");
}

static void ReportsRemoteOptInWithoutTaintedExternalText()
{
    const string source = """
        intent "Reject remote opt-in without tainted text"
        policy PublicDocs {
            classification = public
            inference = remote_opt_in
            capabilities = separated
        }
        schema Summary = {
            text: Text
        }
        fn summarize(doc: ExternalText)
            -> Result<Summary, Error>
            effects [model]
            requires doc != empty
        {
            return model openai "gpt-5.4-mini"
                .task("Summarize this document")
                .input(doc)
                .output<Summary>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2050");
    AssertTrue(diagnostic is not null, "Expected missing tainted external text diagnostic AI2050.");
    AssertTrue(diagnostic!.Message.Contains("external_text = tainted", StringComparison.Ordinal), "Expected diagnostic to explain the required taint policy.");
}

static void ReportsRemoteOptInWithoutSeparatedCapabilities()
{
    const string source = """
        intent "Reject remote opt-in without capability separation"
        policy PublicDocs {
            classification = public
            inference = remote_opt_in
            external_text = tainted
        }
        schema Summary = {
            text: Text
        }
        fn summarize(doc: ExternalText)
            -> Result<Summary, Error>
            effects [model]
            requires doc != empty
        {
            return model openai "gpt-5.4-mini"
                .task("Summarize this document")
                .input(doc)
                .output<Summary>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2051");
    AssertTrue(diagnostic is not null, "Expected missing capability separation diagnostic AI2051.");
    AssertTrue(diagnostic!.Message.Contains("capabilities = separated", StringComparison.Ordinal), "Expected diagnostic to explain the required capability separation policy.");
}

static void ReportsUnsupportedPolicySecurityPostureValues()
{
    const string source = """
        intent "Reject unsupported security policy values"
        policy PublicDocs {
            classification = public
            inference = remote_opt_in
            external_text = trusted
            capabilities = prompt_granted
        }
        fn noop()
            -> Result<Unit, Error>
            requires true
        {
            return Ok(Unit())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2052"), "Expected unsupported external text policy diagnostic AI2052.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2053"), "Expected unsupported capability separation policy diagnostic AI2053.");
}

static void ReportsDuplicateCapabilityDeclarations()
{
    const string source = """
        intent "Duplicate capability"
        capability OrdersApi
        capability OrdersApi
        fn fetch(api: OrdersApi)
            -> Result<Order, OrderError>
            requires api != empty
        {
            return Ok(Order())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2003"), "Expected duplicate capability diagnostic AI2003.");
}

static void ReportsDuplicateResourceDeclarations()
{
    const string source = """
        intent "Duplicate resource"
        resource ModelSession
        resource ModelSession
        fn summarize(session: ModelSession)
            -> Result<Summary, Error>
            requires session != empty
        {
            return Ok(Summary())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2009"), "Expected duplicate resource diagnostic AI2009.");
}

static void ParsesSchemaAndToolDeclarations()
{
    const string source = """
        intent "Tooling primitives"
        capability tmpfs
        schema PlanDraft = {
            region: Region,
            replicas: u8
        }
        tool test_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = [tmpfs]
            timeout = 30s
        fn validate()
            -> Result<PlanDraft, Error>
            requires true
        {
            return Err(MissingPlan)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    AssertTrue(result.Document.Schemas.Single().Name == "PlanDraft", "Schema name was not captured.");
    var tool = result.Document.Tools.Single();
    AssertTrue(tool.Name == "test_patch", "Tool name was not captured.");
    AssertTrue(tool.Parameters.Single() == new ParameterDeclaration("patch", "Patch"), "Tool parameter was not captured.");
    AssertTrue(tool.ReturnType == "TestReport", "Tool return type was not captured.");
    AssertTrue(tool.Sandbox == "wasm", "Tool sandbox was not captured.");
    AssertTrue(tool.Capabilities.SequenceEqual(["tmpfs"]), "Tool capabilities were not captured.");
    AssertTrue(tool.Timeout == "30s", "Tool timeout was not captured.");
}

static void ParsesToolDeclarationsWithMultipleCapabilitiesAndTimeout()
{
    const string source = """
        intent "Tooling primitives"
        capability tmpfs
        capability network
        tool test_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = [tmpfs, network]
            timeout = 45s
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var tool = result.Document.Tools.Single();
    AssertTrue(tool.Sandbox == "wasm", "Tool sandbox was not captured.");
    AssertTrue(tool.Capabilities.SequenceEqual(["tmpfs", "network"]), "Tool capability list was not captured in order.");
    AssertTrue(tool.Timeout == "45s", "Tool timeout was not captured.");
}

static void ReportsDuplicateSchemaDeclarations()
{
    const string source = """
        intent "Duplicate schema"
        schema PlanDraft = {
            region: Region
        }
        schema PlanDraft = {
            monthly_cost: Money
        }
        fn validate()
            -> Result<PlanDraft, Error>
            requires true
        {
            return Err(MissingPlan)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2012"), "Expected duplicate schema diagnostic AI2012.");
}

static void ReportsMissingRequiredToolPolicyFields()
{
    const string source = """
        intent "Missing tool policy"
        tool test_patch(patch: Patch) -> TestReport
            capabilities = [tmpfs]
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2013"), "Expected missing tool sandbox diagnostic AI2013.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2014"), "Expected missing tool timeout diagnostic AI2014.");
}

static void ReportsNonWasmToolSandbox()
{
    const string source = """
        intent "Unsafe tool sandbox"
        tool test_patch(patch: Patch) -> TestReport
            sandbox = host
            timeout = 30s
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2015"), "Expected non-wasm sandbox diagnostic AI2015.");
}

static void ReportsInvalidToolSandboxSyntax()
{
    const string source = """
        intent "Invalid sandbox syntax"
        tool test_patch(patch: Patch) -> TestReport
            sandbox = [wasm]
            timeout = 30s
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2033"), "Expected invalid sandbox syntax diagnostic AI2033.");
}

static void ReportsInvalidToolCapabilitySyntax()
{
    const string source = """
        intent "Invalid capability syntax"
        capability tmpfs
        tool test_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = tmpfs
            timeout = 30s
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2034"), "Expected invalid capability syntax diagnostic AI2034.");
}

static void ReportsDuplicateFunctionDeclarations()
{
    const string source = """
        intent "Duplicate functions"
        fn fetch()
            -> Result<Order, OrderError>
            requires true
        {
            return Err(NetworkFailure)
        }
        fn fetch(customerId: CustomerId)
            -> Result<Order, OrderError>
            requires customerId != empty
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2006"), "Expected duplicate function diagnostic AI2006.");
}

static void ReportsDuplicateUnionCases()
{
    const string source = """
        intent "Duplicate type cases"
        type OrderStatus = Active | Archived | Active
        fn fetch()
            -> Result<Order, OrderError>
            requires true
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2007"), "Expected duplicate union case diagnostic AI2007.");
}

static void ReportsUndeclaredResourceParameter()
{
    const string source = """
        intent "Undeclared resource"
        fn summarize(session: ModelSession)
            -> Result<Summary, Error>
            requires session != empty
        {
            return Err(MissingSession)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2010"), "Expected undeclared resource diagnostic AI2010.");
}

static void ReportsUndeclaredCapabilityParameter()
{
    const string source = """
        intent "Undeclared capability"
        fn fetch(api: OrdersApi)
            -> Result<Order, OrderError>
            requires api != empty
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2008"), "Expected undeclared capability diagnostic AI2008.");
}

static void ReportsUndeclaredFunctionEffect()
{
    const string source = """
        intent "Undeclared function effect"
        effect net
        fn fetch()
            -> Result<Order, OrderError>
            effect(db)
            requires true
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2004"), "Expected undeclared function effect diagnostic AI2004.");
}

static void ReportsUnsupportedObservationKind()
{
    const string source = """
        intent "Unsupported telemetry kind"
        fn fetch()
            -> Result<Order, OrderError>
            requires true
        {
            return Err(NetworkFailure)
        }
        observe {
            gauge "orders.fetch.active"
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2005"), "Expected unsupported observation kind diagnostic AI2005.");
}

static void ReportsUnterminatedStringLocation()
{
    var result = AISharpParser.Parse("intent \"missing close");
    var diagnostic = result.Diagnostics.Single(d => d.Code == "AI0001");
    AssertTrue(diagnostic.Location.Line == 1, "Expected line 1.");
    AssertTrue(diagnostic.Location.Column == 8, "Expected string opening column.");
}

static void AuditsCompleteSpecProgress()
{
    const string markdown = """
        # Spec

        - [x] Parse checklist items.
        - [x] Count progress.
        """;

    var result = SpecProgressAuditor.Audit(markdown);

    AssertTrue(result.CompletedChecklistItems == 2, "Expected two completed checklist items.");
    AssertTrue(result.IncompleteChecklistItems == 0, "Expected no incomplete checklist items.");
    AssertTrue(result.Blockers.Count == 0, "Expected no blockers.");
    AssertTrue(result.Status == SpecProgressStatus.Complete, "Expected complete status.");
}

static void AuditsInProgressSpecProgress()
{
    const string markdown = """
        # Spec

        - [x] Parse checklist items.
        - [ ] Count progress.
        """;

    var result = SpecProgressAuditor.Audit(markdown);

    AssertTrue(result.CompletedChecklistItems == 1, "Expected one completed checklist item.");
    AssertTrue(result.IncompleteChecklistItems == 1, "Expected one incomplete checklist item.");
    AssertTrue(result.Blockers.Count == 0, "Expected no blockers.");
    AssertTrue(result.Status == SpecProgressStatus.InProgress, "Expected in-progress status.");
}

static void AuditsBlockedSpecProgress()
{
    const string markdown = """
        # Spec

        - [x] Parse checklist items.
        - [ ] Count progress.

        BLOCKED: Waiting for API credentials.
        """;

    var result = SpecProgressAuditor.Audit(markdown);

    AssertTrue(result.CompletedChecklistItems == 1, "Expected one completed checklist item.");
    AssertTrue(result.IncompleteChecklistItems == 1, "Expected one incomplete checklist item.");
    AssertTrue(result.Blockers.SequenceEqual(["BLOCKED: Waiting for API credentials."]), "Expected blocker line to be captured.");
    AssertTrue(result.Status == SpecProgressStatus.Blocked, "Expected blocked status.");
}

static void AuditsProseOnlySpecProgressAsInProgress()
{
    const string markdown = """
        # Spec

        This prose-only specification has no GitHub-style checklist task graph.
        It still needs an implementation audit before completion can be claimed.
        """;

    var result = SpecProgressAuditor.Audit(markdown);

    AssertTrue(result.TotalChecklistItems == 0, "Expected no checklist items.");
    AssertTrue(result.CompletedChecklistItems == 0, "Expected no completed checklist items.");
    AssertTrue(result.IncompleteChecklistItems == 0, "Expected no incomplete checklist items.");
    AssertTrue(result.Blockers.Count == 0, "Expected no blockers.");
    AssertTrue(result.Status == SpecProgressStatus.InProgress, "Expected prose-only specs to remain in progress.");
}

static void ParsesLineComments()
{
    const string source = """
        // Module-level comment
        intent "Fetch active orders" // inline comment on intent
        // Capability declaration
        capability OrdersApi // inline comment on capability
        fn fetch(api: OrdersApi) // inline comment on fn
            -> Result<Order, OrderError> // inline comment on return type
            requires api != empty // inline comment on contract
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics when source contains line comments.");
    AssertTrue(result.Document.Intent == "Fetch active orders", "Intent was not captured.");
    AssertTrue(result.Document.Capabilities.Single().Name == "OrdersApi", "Capability was not captured.");
    AssertTrue(result.Document.Functions.Single().Name == "fetch", "Function name was not captured.");
    AssertTrue(result.Document.Functions.Single().Contracts.Count == 1, "Contract was not captured.");
}

static void ParsesSchemaFields()
{
    const string source = """
        intent "Schema field model"
        schema OrderPatch = {
            customerId: CustomerId,
            status: OrderStatus
        }
        fn validate()
            -> Result<OrderPatch, Error>
            requires true
        {
            return Err(MissingPatch)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var schema = result.Document.Schemas.Single();
    AssertTrue(schema.Name == "OrderPatch", "Schema name was not captured.");
    AssertTrue(schema.Fields.Count == 2, "Expected two schema fields.");
    AssertTrue(schema.Fields[0] == new ParameterDeclaration("customerId", "CustomerId"), "First schema field was not captured correctly.");
    AssertTrue(schema.Fields[1] == new ParameterDeclaration("status", "OrderStatus"), "Second schema field was not captured correctly.");
}

static void ReportsUndeclaredToolCapability()
{
    const string source = """
        intent "Tool capability validation"
        tool test_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = [unregistered_cap]
            timeout = 30s
        fn validate()
            -> Result<TestReport, Error>
            requires true
        {
            return Err(MissingReport)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2017"), "Expected undeclared tool capability diagnostic AI2017.");
}

static void ParsesRecordTypeDeclarations()
{
    const string source = """
        intent "Domain record types"
        type Order = {
            id: OrderId,
            status: OrderStatus,
            customerId: CustomerId
        }
        fn process()
            -> Result<Order, Error>
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var t = result.Document.Types.Single();
    AssertTrue(t.Name == "Order", "Record type name was not captured.");
    AssertTrue(t.Kind == TypeKind.Record, "Expected record type kind.");
    AssertTrue(t.Fields.Count == 3, "Expected three fields.");
    AssertTrue(t.Fields[0] == new ParameterDeclaration("id", "OrderId"), "First field was not captured correctly.");
    AssertTrue(t.Fields[1] == new ParameterDeclaration("status", "OrderStatus"), "Second field was not captured correctly.");
    AssertTrue(t.Fields[2] == new ParameterDeclaration("customerId", "CustomerId"), "Third field was not captured correctly.");
}

static void ParsesBracketedEffectsSyntax()
{
    const string source = """
        intent "Bracketed effects"
        effect net
        effect db
        fn fetch()
            -> Result<Order, Error>
            effects [net, db]
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    AssertTrue(result.Document.Functions.Single().Effects.SequenceEqual(["net", "db"]), "Expected bracketed effects to be captured.");
}

static void ReportsUndeclaredEffectInBracketedSyntax()
{
    const string source = """
        intent "Undeclared bracketed effect"
        effect net
        fn fetch()
            -> Result<Order, Error>
            effects [net, db]
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2004"), "Expected undeclared effect diagnostic AI2004 for bracketed syntax.");
}

static void ParsesFirstClassEffectTaxonomyInSignatures()
{
    const string source = """
        intent "First-class effects"
        fn gather()
            -> Result<Report, Error>
            effects [io, time, random, model, network, unsafe_ffi]
            requires true
        {
            return Ok(Report())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected first-class effects to be valid without document-scope declarations.");
    AssertTrue(result.Document.Effects.Count == 0, "Built-in effects should not require document-scope declarations.");
    AssertTrue(
        result.Document.Functions.Single().Effects.SequenceEqual(["io", "time", "random", "model", "network", "unsafe_ffi"]),
        "First-class function effects were not captured in signature order.");
}

static void ReportsDocumentScopeBuiltInEffectDeclarations()
{
    const string source = """
        intent "Misplaced built-in effect"
        effect model
        fn choose()
            -> Result<Plan, Error>
            effects [model]
            requires true
        {
            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2027"), "Expected document-scope built-in effect placement diagnostic AI2027.");
}

static void ParsesInvariantContracts()
{
    const string source = """
        intent "Invariant contract"
        fn allocate()
            -> Result<Plan, Error>
            requires true
            invariant result is Ok => result.value.monthly_cost >= 0
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var contracts = result.Document.Functions.Single().Contracts;
    AssertTrue(contracts.Count == 2, "Expected requires and invariant contracts.");
    AssertTrue(contracts[0].Kind == ContractKind.Requires, "Expected first contract to be requires.");
    AssertTrue(contracts[1].Kind == ContractKind.Invariant, "Expected second contract to be invariant.");
    AssertTrue(contracts[1].Expression == "result is Ok => result.value.monthly_cost >= 0", "Invariant expression was not captured.");
}

static void AcceptsInvariantAsRequiredFunctionContract()
{
    const string source = """
        intent "Invariant only contract"
        fn allocate()
            -> Result<Plan, Error>
            invariant result is Err || result.value.monthly_cost >= 0
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected invariant-only function to satisfy the contract requirement.");
    AssertTrue(result.Document.Functions.Single().Contracts.Single().Kind == ContractKind.Invariant, "Expected invariant contract kind.");
}

static void RepresentsContractCheckSemanticsConsistently()
{
    const string source = """
        intent "Contract semantics"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires order != empty
            ensures result is Ok => result.value.total >= 0
            invariant result is Err || result.value.total >= 0
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var contracts = result.Document.Functions.Single().Contracts;
    AssertTrue(contracts.Select(c => c.Kind).SequenceEqual([ContractKind.Requires, ContractKind.Ensures, ContractKind.Invariant]), "Expected all contract kinds in source order.");
    AssertTrue(contracts.Select(c => c.CheckPhase).SequenceEqual([ContractCheckPhase.Precondition, ContractCheckPhase.Postcondition, ContractCheckPhase.Invariant]), "Contract check phases were not derived consistently.");
    AssertTrue(contracts.All(c => c.EmitRuntimeCheck), "Contracts should be represented as runtime checks.");
    AssertTrue(contracts.All(c => c.EmitDebugCheck), "Contracts should be represented as debug checks.");
    AssertTrue(contracts.All(c => c.EmitVerificationCondition), "Contracts should be represented as CI verification conditions.");
    AssertTrue(contracts.Select(c => c.VerificationHook).SequenceEqual(["requires", "ensures", "invariant"]), "Verification hook names should be stable and lower-case.");
}

static void ReportsEmptyContractExpressionsClearly()
{
    const string source = """
        intent "Malformed contract"
        fn estimate()
            -> Result<Quote, Error>
            requires
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2028"), "Expected empty contract diagnostic AI2028.");
    var diagnostic = result.Diagnostics.Single(d => d.Code == "AI2028");
    AssertTrue(diagnostic.Message.Contains("requires", StringComparison.Ordinal), "Expected diagnostic message to name the malformed contract kind.");
    AssertTrue(diagnostic.Message.Contains("boolean expression", StringComparison.Ordinal), "Expected diagnostic message to explain the recovery.");
    AssertTrue(result.Success == false, "Malformed contracts should fail validation.");
}

static void ContractCheckerRequiresBooleanContractExpressions()
{
    const string source = """
        intent "Non-boolean contract"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires order
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var diagnostic = result.SemanticDiagnostics.SingleOrDefault(d => d.Code == "AI3040");
    AssertTrue(diagnostic is not null, "Expected non-Boolean contract diagnostic AI3040.");
    var confirmedDiagnostic = diagnostic!;
    AssertTrue(confirmedDiagnostic.Message.Contains("requires", StringComparison.Ordinal), "Expected diagnostic to name the contract kind.");
    AssertTrue(confirmedDiagnostic.Severity == DiagnosticSeverity.Error, "Non-Boolean contracts should be error diagnostics.");
    AssertTrue(!result.Success, "Non-Boolean contracts should fail semantic validation.");
}

static void ContractCheckerScopesResultToPostconditions()
{
    const string source = """
        intent "Result contract scope"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires result == empty
            ensures result is Ok => result.value.total >= 0
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var unknownNames = result.SemanticDiagnostics
        .Where(d => d.Code == "AI3041")
        .Select(d => d.Message)
        .ToArray();

    AssertTrue(unknownNames.Any(message => message.Contains("'result'", StringComparison.Ordinal) && message.Contains("requires", StringComparison.Ordinal)),
        "Expected 'result' to be unknown in requires contracts.");
    AssertTrue(!unknownNames.Any(message => message.Contains("'result'", StringComparison.Ordinal) && message.Contains("ensures", StringComparison.Ordinal)),
        "Did not expect 'result' to be unknown in ensures contracts.");
}

static void ContractCheckerReportsUnknownContractNames()
{
    const string source = """
        intent "Unknown contract name"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires missingOrder != empty
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var diagnostic = result.SemanticDiagnostics.SingleOrDefault(d => d.Code == "AI3041");
    AssertTrue(diagnostic is not null, "Expected unknown contract name diagnostic AI3041.");
    var confirmedDiagnostic = diagnostic!;
    AssertTrue(confirmedDiagnostic.Message.Contains("missingOrder", StringComparison.Ordinal), "Expected diagnostic to name the unknown identifier.");
    AssertTrue(confirmedDiagnostic.Severity == DiagnosticSeverity.Error, "Unknown contract names should be error diagnostics.");
}

static void ContractCheckerRecordsRuntimeAndVerificationMetadata()
{
    const string source = """
        intent "Contract metadata"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires order != empty
            ensures result is Ok => result.value.total >= 0
        {
            return Ok(Quote(0))
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    AssertTrue(result.SemanticDiagnostics.All(d => d.Code != "AI3040" && d.Code != "AI3041"), "Expected valid contract metadata source.");

    var infos = result.ContractCheckResult?.ContractInfos.ToArray() ?? [];
    AssertTrue(infos.Length == 2, "Expected two contract info records.");
    AssertTrue(infos[0].AvailableNames.SequenceEqual(["order"]), "Requires should expose only parameters.");
    AssertTrue(infos[1].AvailableNames.SequenceEqual(["order", "result"]), "Ensures should expose parameters and result.");
    AssertTrue(infos[0].RuntimeCheck.Phase == ContractCheckPhase.Precondition, "Requires runtime metadata should be precondition phase.");
    AssertTrue(infos[1].RuntimeCheck.Phase == ContractCheckPhase.Postcondition, "Ensures runtime metadata should be postcondition phase.");
    AssertTrue(infos.All(info => info.RuntimeCheck.Emit), "Runtime checks should be emitted for each contract.");
    AssertTrue(infos.All(info => info.VerificationCondition.Emit), "Verification conditions should be emitted for each contract.");
    AssertTrue(infos.Select(info => info.VerificationCondition.Hook).SequenceEqual(["requires", "ensures"]), "Verification hooks should be stable.");
    AssertTrue(infos.All(info => info.VerificationCondition.Location.Line > 0), "Verification metadata should preserve source locations.");
}

static void ParsesModuleDeclaration()
{
    const string source = """
        module billing.alloc
        intent "Allocate billing resources"
        fn allocate()
            -> Result<Plan, Error>
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    AssertTrue(result.Document.Module == "billing.alloc", "Module path was not captured.");
}

static void ParsesUseDeclarations()
{
    const string source = """
        module billing.alloc
        use std.http
        use std.time
        use ai.core
        intent "Allocate billing resources"
        fn allocate()
            -> Result<Plan, Error>
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    AssertTrue(result.Document.Module == "billing.alloc", "Module path was not captured.");
    AssertTrue(result.Document.Uses.Count == 3, "Expected three use declarations.");
    AssertTrue(result.Document.Uses[0].Path == "std.http", "First use path was not captured.");
    AssertTrue(result.Document.Uses[1].Path == "std.time", "Second use path was not captured.");
    AssertTrue(result.Document.Uses[2].Path == "ai.core", "Third use path was not captured.");
}

static void ReportsDuplicateModuleDeclarations()
{
    const string source = """
        module billing.alloc
        module billing.alloc
        intent "Duplicate module"
        fn allocate()
            -> Result<Plan, Error>
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2018"), "Expected duplicate module diagnostic AI2018.");
}

static void ReportsDuplicateUseDeclarations()
{
    const string source = """
        module billing.alloc
        use std.http
        use std.http
        intent "Duplicate use"
        fn allocate()
            -> Result<Plan, Error>
            requires true
        {
            return Err(NotFound)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2019"), "Expected duplicate use diagnostic AI2019.");
}

static void ParsesOwnershipQualifiedResourceParameters()
{
    const string source = """
        intent "Resource ownership"
        resource ModelSession
        resource Secret<T>
        fn summarize(session: owned ModelSession, key: borrowed Secret<ApiKey>)
            -> Result<Summary, Error>
            requires session != empty
        {
            return Err(MissingSummary)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var parameters = result.Document.Functions.Single().Parameters;
    AssertTrue(parameters[0] == new ParameterDeclaration("session", "ModelSession", ParameterOwnership.Owned), "Expected owned session parameter.");
    AssertTrue(parameters[1] == new ParameterDeclaration("key", "Secret<ApiKey>", ParameterOwnership.Borrowed), "Expected borrowed generic secret parameter.");
}

static void ParsesCapabilityBearingFileAndSocketResources()
{
    const string source = """
        intent "Resource handles"
        resource File
        resource Socket
        resource ForeignHandle
        fn bridge(input: borrowed File, output: owned Socket, handle: borrowed ForeignHandle)
            -> Result<Bytes, Error>
            requires input != empty
        {
            return Err(MissingBytes)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics for declared file/socket/foreign handle resources.");
    var parameters = result.Document.Functions.Single().Parameters;
    AssertTrue(parameters[0] == new ParameterDeclaration("input", "File", ParameterOwnership.Borrowed), "Expected borrowed file parameter.");
    AssertTrue(parameters[1] == new ParameterDeclaration("output", "Socket", ParameterOwnership.Owned), "Expected owned socket parameter.");
    AssertTrue(parameters[2] == new ParameterDeclaration("handle", "ForeignHandle", ParameterOwnership.Borrowed), "Expected borrowed foreign handle parameter.");
}

static void ReportsUndeclaredOwnershipQualifiedResourceParameter()
{
    const string source = """
        intent "Undeclared owned resource"
        fn summarize(session: owned ModelSession)
            -> Result<Summary, Error>
            requires session != empty
        {
            return Err(MissingSummary)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2010"), "Expected undeclared owned resource diagnostic AI2010.");
}

static void ReportsOwnershipQualifiersOnNonResourceParameters()
{
    const string source = """
        intent "Invalid ownership"
        capability OrdersApi
        fn fetch(api: borrowed OrdersApi, label: owned Text)
            -> Result<Order, Error>
            requires api != empty
        {
            return Err(NetworkFailure)
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostics = result.Diagnostics.Where(d => d.Code == "AI2029").ToArray();
    AssertTrue(diagnostics.Length == 2, "Expected diagnostics for ownership qualifiers on capability and ordinary values.");
    AssertTrue(diagnostics[0].Message.Contains("api", StringComparison.Ordinal), "Expected diagnostic to name the capability parameter.");
    AssertTrue(diagnostics[1].Message.Contains("label", StringComparison.Ordinal), "Expected diagnostic to name the ordinary parameter.");
}

static void ParsesAsyncFunctionDeclarations()
{
    const string source = """
        intent "Async declarations"
        async fn fetch_order(orderId: OrderId)
            -> Result<Order, Error>
            effects [network]
            requires orderId != empty
        {
            let order = await load_order(orderId)
            return Ok(order)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var function = result.Document.Functions.Single();
    AssertTrue(function.Name == "fetch_order", "Async function name was not captured.");
    AssertTrue(function.IsAsync, "Async function declaration was not marked in the model.");
    AssertTrue(function.Effects.SequenceEqual(["network"]), "Async function effects were not captured.");
}

static void ParsesActorDeclarationsWithStateAndAsyncHandlers()
{
    const string source = """
        intent "Actor profile"
        actor BuildWorker {
            state current: Option<Job>
            state attempts: u32

            async fn handle(job: Job)
                -> Result<Artifact, Error>
                effects [io, time]
                requires job != empty
            {
                let src = await fetch(job.source)
                let out = await compile(src)
                return Ok(out)
            }
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var actor = result.Document.Actors.Single();
    AssertTrue(actor.Name == "BuildWorker", "Actor name was not captured.");
    AssertTrue(actor.RuntimeProfile == "actor", "Actor runtime profile was not captured.");
    AssertTrue(actor.StateMembers.Count == 2, "Expected two actor state members.");
    AssertTrue(actor.StateMembers[0].Name == "current" && actor.StateMembers[0].Type == "Option<Job>", "First actor state member was not captured.");
    AssertTrue(actor.StateMembers[1].Name == "attempts" && actor.StateMembers[1].Type == "u32", "Second actor state member was not captured.");

    var handler = actor.Handlers.Single();
    AssertTrue(handler.Name == "handle", "Actor handler name was not captured.");
    AssertTrue(handler.IsAsync, "Actor handler should be async.");
    AssertTrue(handler.Parameters.Single() == new ParameterDeclaration("job", "Job"), "Actor handler parameter was not captured.");
    AssertTrue(handler.ReturnType == "Result<Artifact, Error>", "Actor handler return type was not captured.");
    AssertTrue(handler.Effects.SequenceEqual(["io", "time"]), "Actor handler effects were not captured.");
    AssertTrue(handler.Contracts.Single().Kind == ContractKind.Requires, "Actor handler contract was not captured.");
    AssertTrue(handler.Body.Statements.OfType<LetStatement>().Count() == 2, "Actor handler let statements were not parsed.");
    AssertTrue(handler.Body.Statements.OfType<ReturnStatement>().Single().Expression.StaticType == "Result<Artifact, Error>", "Actor handler return statement static type was not captured.");
}

static void ParsesStructuredConcurrencyStatements()
{
    const string source = """
        intent "Structured concurrency"
        async fn summarize(customerId: CustomerId)
            -> Result<Summary, Error>
            effects [network]
            requires customerId != empty
        {
            taskgroup fetches {
                spawn profile = fetch_profile(customerId)
                spawn orders = fetch_orders(customerId)
                all profile, orders
            }

            return Ok(Summary(profile, orders))
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var function = result.Document.Functions.Single();
    AssertTrue(function.IsAsync, "Function should be marked async.");
    AssertTrue(function.Body.Statements.Count == 2, "Expected taskgroup and return statements.");

    var taskGroup = (TaskGroupStatement)function.Body.Statements[0];
    AssertTrue(taskGroup.Name == "fetches", "Taskgroup name was not captured.");
    AssertTrue(taskGroup.Statements.Count == 3, "Expected two spawns and one all statement inside taskgroup.");

    var firstSpawn = (SpawnStatement)taskGroup.Statements[0];
    AssertTrue(firstSpawn.Name == "profile", "First spawn binding name was not captured.");
    AssertTrue(firstSpawn.Expression.Text == "fetch_profile(customerId)", "First spawn expression was not captured.");

    var secondSpawn = (SpawnStatement)taskGroup.Statements[1];
    AssertTrue(secondSpawn.Name == "orders", "Second spawn binding name was not captured.");
    AssertTrue(secondSpawn.Expression.Text == "fetch_orders(customerId)", "Second spawn expression was not captured.");

    var all = (AllStatement)taskGroup.Statements[2];
    AssertTrue(all.Targets.SequenceEqual(["profile", "orders"]), "All statement targets were not captured in order.");
}

static void ParsesTypedExpressionOrientedFunctionBodies()
{
    const string source = """
        intent "Typed expression body"
        schema PlanDraft = {
            region: Region
        }
        fn choose_plan(order: Order)
            -> Result<PlanDraft, Error>
            effects [model]
            requires order != empty
        {
            let draft: PlanDraft =
                model local "qwen3-coder"
                    .input(order)
                    .output<PlanDraft>()
                    .run()?

            return Ok(draft)
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var function = result.Document.Functions.Single();
    AssertTrue(function.Body.Statements.Count == 2, "Expected let and return statements.");

    var letStatement = (LetStatement)function.Body.Statements[0];
    AssertTrue(letStatement.Name == "draft", "Let binding name was not captured.");
    AssertTrue(letStatement.TypeAnnotation == "PlanDraft", "Let binding type annotation was not captured.");
    AssertTrue(letStatement.Initializer.StaticType == "PlanDraft", "Let initializer static type should come from the explicit annotation.");
    AssertTrue(letStatement.Initializer.Text.StartsWith("model local \"qwen3-coder\"", StringComparison.Ordinal), "Multiline model expression was not captured.");
    AssertTrue(function.ModelCalls.Single().OutputSchema == "PlanDraft", "Model output schema should still be captured from typed body expressions.");

    var returnStatement = (ReturnStatement)function.Body.Statements[1];
    AssertTrue(returnStatement.Expression.Text == "Ok(draft)", "Return expression text was not captured.");
    AssertTrue(returnStatement.Expression.StaticType == "Result<PlanDraft, Error>", "Return expression static type should be the function return type.");
}

static void ParsesSchemaConstrainedModelCallChains()
{
    const string source = """
        intent "Capture model calls"
        schema PlanDraft = {
            region: Region,
            replicas: u8,
            monthly_cost: Money
        }
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local "qwen3-coder"
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .output<PlanDraft>()
                    .replay(seed: 7, temperature: 0.0)
                    .capture_provenance()
                    .run()?

            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var modelCall = result.Document.Functions.Single().ModelCalls.Single();
    AssertTrue(modelCall.Provider == "local", "Model provider was not captured.");
    AssertTrue(modelCall.Model == "qwen3-coder", "Model id was not captured.");
    AssertTrue(modelCall.Task == "Produce a deployment plan under budget", "Model task was not captured.");
    AssertTrue(modelCall.Input == "order", "Model input expression was not captured.");
    AssertTrue(modelCall.OutputSchema == "PlanDraft", "Schema-constrained output was not captured.");
    var replay = modelCall.Replay;
    AssertTrue(replay is not null, "Replay settings were not captured.");
    AssertTrue(replay!.Arguments == "seed: 7, temperature: 0.0", "Replay arguments were not captured.");
    AssertTrue(modelCall.CapturesProvenance, "Provenance capture was not detected.");
}

static void RepresentsProvenanceRuntimeMetadata()
{
    var replay = new ModelReplayDeclaration("seed: 7, temperature: 0.0", 7, 0.0m);
    var metadata = new ProvenanceMetadataDeclaration(
        "patch-plan-v1",
        "local",
        "qwen3-coder",
        replay,
        [
            new ToolInvocationMetadata(
                "test_patch",
                "wasm",
                "patch",
                "TestReport",
                0,
                TimeSpan.FromSeconds(3))
        ],
        [
            new FileHashMetadata(
                "src/orders.ai",
                "sha256",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
        ]);

    AssertTrue(metadata.PromptTemplateId == "patch-plan-v1", "Prompt template id was not represented.");
    AssertTrue(metadata.Provider == "local", "Model provider was not represented in provenance metadata.");
    AssertTrue(metadata.Model == "qwen3-coder", "Model id was not represented in provenance metadata.");
    AssertTrue(metadata.Replay == replay, "Replay metadata was not represented.");
    AssertTrue(metadata.ToolInvocations.Single().Name == "test_patch", "Tool invocation metadata was not represented.");
    AssertTrue(metadata.ToolInvocations.Single().Duration == TimeSpan.FromSeconds(3), "Tool invocation duration was not represented.");
    AssertTrue(metadata.FileHashes.Single().Algorithm == "sha256", "File hash algorithm was not represented.");
    AssertTrue(metadata.FileHashes.Single().Value.Length == 64, "File hash value was not represented.");
}

static void ParsesModelOutputSchemaReferences()
{
    const string source = """
        intent "Reference model output schemas"
        schema PatchPlan = {
            files: List<SourceFile>,
            commands: List<ToolCommand>
        }
        fn propose_patch(change: ChangeRequest)
            -> Result<PatchPlan, Error>
            effects [model]
            requires change != empty
        {
            return model local "qwen3-coder"
                .task("Produce a reviewed patch plan")
                .input(change)
                .output<PatchPlan>()
                .run()
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");
    var reference = result.Document.Functions.Single().ModelCalls.Single().OutputSchemaReference;
    AssertTrue(reference is not null, "Expected model call to expose output schema reference metadata.");
    AssertTrue(reference!.Name == "PatchPlan", "Output schema reference name was not captured.");
    AssertTrue(reference.Location.Line == 14, "Output schema reference location should point at the .output<Schema>() call.");
    AssertTrue(reference.Location.Column == 9, "Output schema reference column should point at the .output<Schema>() call.");
}

static void ReportsModelCallsUsingUndeclaredOutputSchemas()
{
    const string source = """
        intent "Validate model call schema"
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local "qwen3-coder"
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .output<MissingDraft>()
                    .capture_provenance()
                    .run()?

            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "AI2026"), "Expected undeclared model output schema diagnostic AI2026.");
}

static void ReportsModelCallsMissingModelIds()
{
    const string source = """
        intent "Validate missing model id"
        schema PlanDraft = {
            region: Region
        }
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .output<PlanDraft>()
                    .run()?

            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2030");
    AssertTrue(diagnostic is not null, "Expected missing model id diagnostic AI2030.");
    AssertTrue(diagnostic!.Message.Contains("model id", StringComparison.Ordinal), "Expected diagnostic to explain that a quoted model id is required.");
}

static void ReportsModelCallsMissingOutputSchemas()
{
    const string source = """
        intent "Validate missing model output"
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local "qwen3-coder"
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .run()?

            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2031");
    AssertTrue(diagnostic is not null, "Expected missing model output schema diagnostic AI2031.");
    AssertTrue(diagnostic!.Message.Contains(".output<Schema>()", StringComparison.Ordinal), "Expected diagnostic to explain that a schema-constrained output is required.");
}

static void ReportsModelCallsMissingRunInvocations()
{
    const string source = """
        intent "Validate missing model run"
        schema PlanDraft = {
            region: Region
        }
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local "qwen3-coder"
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .output<PlanDraft>()

            return Ok(Plan())
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Code == "AI2032");
    AssertTrue(diagnostic is not null, "Expected missing model run invocation diagnostic AI2032.");
    AssertTrue(diagnostic!.Message.Contains(".run()", StringComparison.Ordinal), "Expected diagnostic to explain that the model chain must be invoked.");
}

static void IgnoresModelWordsInsideOrdinaryExpressions()
{
    const string source = """
        intent "Ignore ordinary model text"
        fn describe()
            -> Result<Text, Error>
            requires true
        {
            return Ok("model text")
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(!result.Diagnostics.Any(d => d.Code is "AI2030" or "AI2031" or "AI2032"), "Expected model-call diagnostics to apply only to model-call expressions.");
}

static void ParsesTypedAstMacroAndDeterministicIncrementalGeneratorDeclarations()
{
    const string source = """
        intent "Expose compile-time metaprogramming"
        macro derive_encoder(input: Ast.Record) -> Ast.Declaration
            input = typed_ast
            expands = declarations
        {
            return ast.declaration(input.name + "Encoder")
        }
        generator schema_index(module: Ast.Module) -> GeneratedFile
            incremental = true
            deterministic = true
            tracks = [schemas, tools]
            output = "generated/schema_index.ai"
        {
            return emit_index(module.schemas)
        }
        fn validate()
            -> Result<Unit, Error>
            requires true
        {
            return Ok(Unit())
        }
        """;

    var result = AISharpParser.Parse(source);

    AssertTrue(result.Diagnostics.Count == 0, "Expected no diagnostics.");

    var macro = result.Document.Macros.Single();
    AssertTrue(macro.Name == "derive_encoder", "Macro name was not captured.");
    AssertTrue(macro.Parameters.Single() == new ParameterDeclaration("input", "Ast.Record"), "Macro typed AST parameter was not captured.");
    AssertTrue(macro.ReturnType == "Ast.Declaration", "Macro return type was not captured.");
    AssertTrue(macro.Input == "typed_ast", "Macro input model was not captured.");
    AssertTrue(macro.Expands == "declarations", "Macro expansion target was not captured.");
    AssertTrue(macro.Body.Text.Contains("ast.declaration", StringComparison.Ordinal), "Macro body text was not retained for later lowering.");

    var gen = result.Document.Generators.Single();
    AssertTrue(gen.Name == "schema_index", "Generator name was not captured.");
    AssertTrue(gen.Parameters.Single() == new ParameterDeclaration("module", "Ast.Module"), "Generator typed AST parameter was not captured.");
    AssertTrue(gen.ReturnType == "GeneratedFile", "Generator return type was not captured.");
    AssertTrue(gen.Incremental, "Generator incremental flag was not captured.");
    AssertTrue(gen.Deterministic, "Generator deterministic flag was not captured.");
    AssertTrue(gen.Tracks.SequenceEqual(["schemas", "tools"]), "Generator tracked dependency list was not captured in order.");
    AssertTrue(gen.Output == "generated/schema_index.ai", "Generator output path was not captured.");
    AssertTrue(gen.Body.Text.Contains("emit_index", StringComparison.Ordinal), "Generator body text was not retained for later lowering.");
}

static void DocumentsCompilerPipelineStageBoundaries()
{
    var boundaries = AISharpCompilerPipeline.Boundaries;

    AssertTrue(typeof(IAstArtifact).IsAssignableFrom(typeof(AISharpDocument)), "AISharpDocument should be the explicit parser AST output contract.");
    AssertTrue(boundaries.Count == 4, "Expected parser->AST, AST->HIR, HIR->typed MIR, and typed MIR->lowering boundaries.");
    AssertTrue(boundaries.Select(boundary => boundary.Name).SequenceEqual([
        "parse-source-to-ast",
        "build-ast-to-hir",
        "check-hir-to-typed-mir",
        "lower-typed-mir-to-target"
    ]), "Pipeline boundary names should be stable and ordered.");

    AssertTrue(boundaries[0].From == CompilerPipelineStage.SourceText, "Parser boundary should start from source text.");
    AssertTrue(boundaries[0].To == CompilerPipelineStage.Ast, "Parser boundary should produce AST.");
    AssertTrue(boundaries[0].InputContract == typeof(string), "Parser boundary should accept source text.");
    AssertTrue(boundaries[0].OutputContract == typeof(AISharpDocument), "Parser boundary should produce AISharpDocument.");

    AssertTrue(boundaries[1].InputContract == typeof(IAstArtifact), "HIR boundary should consume the AST interface.");
    AssertTrue(boundaries[1].OutputContract == typeof(IHirArtifact), "HIR boundary should produce the HIR interface.");
    AssertTrue(boundaries[2].InputContract == typeof(IHirArtifact), "Typed MIR boundary should consume the HIR interface.");
    AssertTrue(boundaries[2].OutputContract == typeof(ITypedMirArtifact), "Typed MIR boundary should produce the typed MIR interface.");
    AssertTrue(boundaries[3].InputContract == typeof(ITypedMirArtifact), "Lowering boundary should consume the typed MIR interface.");
    AssertTrue(boundaries[3].OutputContract == typeof(ILoweredArtifact), "Lowering boundary should produce the lowered artifact interface.");

    AssertTrue(boundaries.All(boundary => boundary.RequiredContext == CompilerBoundaryContext.CurrentTaskSlice), "Compiler tasks should only require the current task slice, not a full spec prompt reload.");
    AssertTrue(boundaries.All(boundary => !string.IsNullOrWhiteSpace(boundary.Responsibility)), "Every boundary should document its phase responsibility.");
    AssertTrue(boundaries.All(boundary => !string.IsNullOrWhiteSpace(boundary.HandOffContract)), "Every boundary should document its hand-off contract.");
}

static void RepresentsLoweredRoslynArtifactMetadata()
{
    var metadata = new LoweredArtifactMetadata(
        LoweredArtifactTarget.RoslynCSharp,
        "orders.ai.cs",
        new SourceMapReference("samples/orders.aisharp", 12, 5),
        requiresAnalyzerMetadata: true,
        provenanceMetadataKeys: ["promptTemplateId", "model", "fileHashes"],
        analyzerMetadataKeys: ["ai.effects", "ai.contracts", "ai.provenance"]);

    var artifact = new RoslynLoweredArtifact(
        "public static partial class Orders { }",
        metadata);

    AssertTrue(artifact is ILoweredArtifact, "Roslyn lowered artifacts should satisfy the lowering boundary output contract.");
    AssertTrue(artifact.Metadata.Target == LoweredArtifactTarget.RoslynCSharp, "Lowered artifact target should identify Roslyn C#.");
    AssertTrue(artifact.Metadata.GeneratedPath == "orders.ai.cs", "Generated path should be retained for Roslyn syntax tree creation.");
    AssertTrue(artifact.Metadata.SourceMap.SourcePath == "samples/orders.aisharp", "Original AI# source path should be retained for diagnostics.");
    AssertTrue(artifact.Metadata.SourceMap.Line == 12, "Source map line should be retained for analyzer diagnostics.");
    AssertTrue(artifact.Metadata.RequiresAnalyzerMetadata, "Roslyn lowered artifacts should mark analyzer metadata as required.");
    AssertTrue(artifact.Metadata.ProvenanceMetadataKeys.SequenceEqual(["promptTemplateId", "model", "fileHashes"]), "Provenance metadata keys should be stable and ordered.");
    AssertTrue(artifact.Metadata.AnalyzerMetadataKeys.SequenceEqual(["ai.effects", "ai.contracts", "ai.provenance"]), "Analyzer metadata keys should be stable and ordered.");
}

static void DocumentsRoslynAnalyzerMetadataHooks()
{
    var hook = new RoslynAnalyzerHook(
        "AI9001",
        "ai.provenance",
        "Require provenance metadata before lowered model-call output is consumed by generated C#",
        CompilerPipelineStage.LoweredTarget,
        requiredMetadataKeys: ["ai.provenance", "ai.sourceMap"]);

    AssertTrue(hook.DiagnosticId == "AI9001", "Analyzer hook diagnostic id should be captured.");
    AssertTrue(hook.MetadataKey == "ai.provenance", "Analyzer hook should name the primary metadata key.");
    AssertTrue(hook.Stage == CompilerPipelineStage.LoweredTarget, "Roslyn analyzer hooks should attach at the lowered target stage.");
    AssertTrue(hook.RequiredMetadataKeys.SequenceEqual(["ai.provenance", "ai.sourceMap"]), "Analyzer hook required metadata keys should be stable and ordered.");
    AssertTrue(AISharpCompilerPipeline.RoslynAnalyzerHooks.Contains(hook), "Compiler pipeline should publish the Roslyn analyzer hook contract.");
}

static void RepresentsCiReleaseIntegrityGates()
{
    var gates = AISharpCompilerPipeline.ReleaseIntegrityGates;

    AssertTrue(gates.Select(gate => gate.Kind).SequenceEqual([
        ReleaseIntegrityGateKind.Build,
        ReleaseIntegrityGateKind.Test,
        ReleaseIntegrityGateKind.Provenance,
        ReleaseIntegrityGateKind.Sbom,
        ReleaseIntegrityGateKind.Signing,
        ReleaseIntegrityGateKind.Publish
    ]), "Release integrity gates should be stable and ordered from build through publish.");

    var buildGate = gates.Single(gate => gate.Kind == ReleaseIntegrityGateKind.Build);
    AssertTrue(buildGate.RequiredEvidenceKeys.SequenceEqual(["build.command", "build.exitCode", "build.artifactHashes"]), "Build gate should require command, result, and artifact hash evidence.");
    AssertTrue(buildGate.BlockingFailureMode.Contains("Reject", StringComparison.Ordinal), "Build gate should describe a blocking failure mode.");

    var testGate = gates.Single(gate => gate.Kind == ReleaseIntegrityGateKind.Test);
    AssertTrue(testGate.RequiredEvidenceKeys.SequenceEqual(["test.command", "test.passed", "test.failed"]), "Test gate should require test command and pass/fail evidence.");

    var provenanceGate = gates.Single(gate => gate.Kind == ReleaseIntegrityGateKind.Provenance);
    AssertTrue(provenanceGate.RequiredEvidenceKeys.SequenceEqual(["provenance.format", "provenance.builderId", "provenance.subjectDigest"]), "Provenance gate should require builder and subject evidence.");

    var signingGate = gates.Single(gate => gate.Kind == ReleaseIntegrityGateKind.Signing);
    AssertTrue(signingGate.RequiredEvidenceKeys.Contains("signing.transparencyLog", StringComparer.Ordinal), "Signing gate should require transparency log evidence.");
}

static void RepresentsReleaseArtifactProvenanceSbomAndSigningMetadata()
{
    var artifact = new ReleaseArtifactMetadata(
        "artifacts/aisharp.1.0.0.nupkg",
        "nuget",
        "1.0.0",
        [
            new FileHashMetadata(
                "artifacts/aisharp.1.0.0.nupkg",
                "sha256",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        ]);

    var build = new BuildEvidenceMetadata(
        "dotnet build AISharp.sln",
        "Release",
        ExitCode: 0,
        ArtifactHashes: artifact.Hashes);

    var tests = new TestEvidenceMetadata(
        "dotnet test AISharp.sln --no-restore -m:1 /nr:false",
        Passed: 104,
        Failed: 0);

    var provenance = new ReleaseProvenanceMetadata(
        "slsa-provenance-v1",
        "github-actions/aisharp-release",
        "refs/tags/v1.0.0",
        artifact.Hashes.Single().Value,
        "artifacts/aisharp.1.0.0.intoto.jsonl");

    var sbom = new SbomMetadata(
        "CycloneDX",
        "1.6",
        "artifacts/aisharp.1.0.0.cdx.json",
        artifact.Hashes.Single().Value);

    var signing = new SigningMetadata(
        "sigstore",
        "https://github.com/doelrgarcia/AISharp/.github/workflows/release.yml@refs/tags/v1.0.0",
        "rekor",
        "artifacts/aisharp.1.0.0.nupkg.sig");

    var envelope = new ReleaseIntegrityEnvelope(
        artifact,
        build,
        tests,
        provenance,
        sbom,
        signing,
        AISharpCompilerPipeline.ReleaseIntegrityGates);

    AssertTrue(envelope.Artifact.Version == "1.0.0", "Release artifact version should be represented.");
    AssertTrue(envelope.Build.ExitCode == 0, "Build evidence should retain the process exit code.");
    AssertTrue(envelope.Tests.Failed == 0, "Test evidence should retain failure counts for release gating.");
    AssertTrue(envelope.Provenance.SubjectDigest == artifact.Hashes.Single().Value, "Provenance subject digest should match the release artifact hash.");
    AssertTrue(envelope.Sbom.Format == "CycloneDX", "SBOM format should be represented.");
    AssertTrue(envelope.Signing.Provider == "sigstore", "Signing provider should be represented.");
    AssertTrue(envelope.Gates.Count == 6, "Release envelope should carry the full release gate plan.");
}

static void LanguageServerPublishesCompilerDiagnostics()
{
    const string source = """
        intent "Editor diagnostics"
        fn broken(api: MissingApi)
            -> Result<Unit, Error>
            requires true
        {
            return Ok(Unit())
        }
        """;

    var result = AISharpLanguageServer.GetDiagnostics(
        new LanguageServerTextDocument("file:///workspace/orders.aisharp", source, Version: 7));

    var diagnostic = result.Diagnostics.Single(d => d.Code == "AI2008");

    AssertTrue(result.Uri == "file:///workspace/orders.aisharp", "Diagnostics result should retain the editor document URI.");
    AssertTrue(result.Version == 7, "Diagnostics result should retain the editor document version.");
    AssertTrue(diagnostic.Severity == LanguageServerDiagnosticSeverity.Error, "Compiler errors should map to LSP error diagnostics.");
    AssertTrue(diagnostic.Source == "aisharp", "Diagnostics should identify the AI# language source.");
    AssertTrue(diagnostic.Message.Contains("MissingApi", StringComparison.Ordinal), "Diagnostic message should come from the compiler.");
    AssertTrue(diagnostic.Range.Start.Line == 1, "LSP diagnostic lines should be zero-based.");
    AssertTrue(diagnostic.Range.Start.Character == 15, "LSP diagnostic columns should be zero-based.");
    AssertTrue(diagnostic.Range.End.Line == diagnostic.Range.Start.Line, "Fallback diagnostic range should stay on the source line.");
    AssertTrue(diagnostic.Range.End.Character > diagnostic.Range.Start.Character, "Fallback diagnostic range should select at least one character.");
}

static void LanguageServerAdvertisesCompilerMetadataCapabilities()
{
    var capabilities = AISharpLanguageServer.Capabilities;

    AssertTrue(capabilities.LanguageId == "aisharp", "Language server should advertise the AI# language id.");
    AssertTrue(capabilities.DiagnosticsProvider, "Language server should advertise pull diagnostics support.");
    AssertTrue(capabilities.DocumentSymbolProvider, "Language server should advertise document symbols from compiler metadata.");
    AssertTrue(capabilities.MetadataKinds.SequenceEqual([
        "sourceMap",
        "diagnostics",
        "declarations",
        "effects",
        "contracts",
        "modelCalls",
        "provenance",
        "schemas",
        "tools"
    ]), "Language server metadata kinds should be stable and ordered for editor integrations.");
}

static void ReportsDisallowedCompileTimeSideEffectsInMacroAndGeneratorBodies()
{
    const string source = """
        intent "Reject compile-time side effects"
        macro stamp(input: Ast.Record) -> Ast.Declaration
            input = typed_ast
            expands = declarations
        {
            let now = time.now()
            return ast.declaration(input.name + now)
        }
        generator remote_index(module: Ast.Module) -> GeneratedFile
            incremental = true
            deterministic = true
            tracks = [schemas]
            output = "generated/remote.ai"
        {
            let draft = model local "qwen3-coder"
                .output<GeneratedFile>()
                .run()
            return draft
        }
        fn validate()
            -> Result<Unit, Error>
            requires true
        {
            return Ok(Unit())
        }
        """;

    var result = AISharpParser.Parse(source);

    var diagnostics = result.Diagnostics.Where(d => d.Code == "AI2044").ToArray();
    AssertTrue(diagnostics.Length == 2, "Expected compile-time side-effect diagnostic AI2044 for both macro and generator bodies.");
    AssertTrue(diagnostics.Any(d => d.Message.Contains("macro 'stamp'", StringComparison.Ordinal)), "Expected diagnostic to identify the side-effecting macro.");
    AssertTrue(diagnostics.Any(d => d.Message.Contains("generator 'remote_index'", StringComparison.Ordinal)), "Expected diagnostic to identify the side-effecting generator.");
}

static void CliJsonEmitsPolicyFields()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-policy-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose policy JSON"
        policy CustomerPII {
            classification = secret
            inference = local_only
            external_text = tainted
            capabilities = separated
            training = forbidden
            telemetry = opt_in_aggregate
        }
        fn redact()
            -> Result<Text, Error>
            requires true
        {
            return Ok(Text())
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var policy = json.RootElement.GetProperty("policies").EnumerateArray().Single();
        AssertTrue(policy.GetProperty("name").GetString() == "CustomerPII", "Policy name was not emitted.");
        AssertTrue(policy.GetProperty("classification").GetString() == "secret", "Policy classification property was not emitted.");
        AssertTrue(policy.GetProperty("inference").GetString() == "local_only", "Policy inference property was not emitted.");
        AssertTrue(policy.GetProperty("externalText").GetString() == "tainted", "Policy externalText property was not emitted.");
        AssertTrue(policy.GetProperty("capabilities").GetString() == "separated", "Policy capabilities property was not emitted.");
        AssertTrue(policy.GetProperty("training").GetString() == "forbidden", "Policy training property was not emitted.");
        AssertTrue(policy.GetProperty("telemetry").GetString() == "opt_in_aggregate", "Policy telemetry property was not emitted.");

        var fields = policy.GetProperty("fields")
            .EnumerateArray()
            .ToDictionary(
                field => field.GetProperty("key").GetString() ?? string.Empty,
                field => field.GetProperty("value").GetString() ?? string.Empty,
                StringComparer.Ordinal);

        AssertTrue(fields["classification"] == "secret", "Policy classification field was not emitted.");
        AssertTrue(fields["inference"] == "local_only", "Policy inference field was not emitted.");
        AssertTrue(fields["external_text"] == "tainted", "Policy external_text field was not emitted.");
        AssertTrue(fields["capabilities"] == "separated", "Policy capabilities field was not emitted.");
        AssertTrue(fields["training"] == "forbidden", "Policy training field was not emitted.");
        AssertTrue(fields["telemetry"] == "opt_in_aggregate", "Policy telemetry field was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsSchemaFields()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-schema-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose schema JSON"
        schema OrderPatch = {
            customerId: CustomerId,
            status: OrderStatus
        }
        fn validate()
            -> Result<OrderPatch, Error>
            requires true
        {
            return Err(MissingPatch)
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var schema = json.RootElement.GetProperty("schemas").EnumerateArray().Single();
        AssertTrue(schema.GetProperty("name").GetString() == "OrderPatch", "Schema name was not emitted.");

        var fields = schema.GetProperty("fields")
            .EnumerateArray()
            .Select(field => new
            {
                Name = field.GetProperty("name").GetString(),
                Type = field.GetProperty("type").GetString()
            })
            .ToArray();

        AssertTrue(fields.Length == 2, "Expected two schema fields.");
        AssertTrue(fields[0].Name == "customerId" && fields[0].Type == "CustomerId", "First schema field was not emitted.");
        AssertTrue(fields[1].Name == "status" && fields[1].Type == "OrderStatus", "Second schema field was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsAsyncAndStructuredConcurrencySyntax()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-async-concurrency-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose async and structured concurrency JSON"
        async fn summarize(customerId: CustomerId)
            -> Result<Summary, Error>
            effects [network]
            requires customerId != empty
        {
            taskgroup fetches {
                spawn profile = fetch_profile(customerId)
                spawn orders = fetch_orders(customerId)
                all profile, orders
            }

            return Ok(Summary(profile, orders))
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var function = json.RootElement.GetProperty("functions").EnumerateArray().Single();
        AssertTrue(function.GetProperty("isAsync").GetBoolean(), "Async function flag was not emitted.");

        var statements = function.GetProperty("body").GetProperty("statements").EnumerateArray().ToArray();
        AssertTrue(statements[0].GetProperty("kind").GetString() == "taskgroup", "Expected taskgroup statement kind.");
        AssertTrue(statements[0].GetProperty("name").GetString() == "fetches", "Taskgroup name was not emitted.");

        var childStatements = statements[0].GetProperty("statements").EnumerateArray().ToArray();
        AssertTrue(childStatements[0].GetProperty("kind").GetString() == "spawn", "Expected spawn statement kind.");
        AssertTrue(childStatements[0].GetProperty("name").GetString() == "profile", "Spawn name was not emitted.");
        AssertTrue(childStatements[0].GetProperty("expression").GetProperty("text").GetString() == "fetch_profile(customerId)", "Spawn expression was not emitted.");
        AssertTrue(childStatements[2].GetProperty("kind").GetString() == "all", "Expected all statement kind.");
        AssertTrue(childStatements[2].GetProperty("targets").EnumerateArray().Select(target => target.GetString()).SequenceEqual(["profile", "orders"]), "All targets were not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsActorDeclarations()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-actor-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose actor JSON"
        actor BuildWorker {
            state current: Option<Job>

            async fn handle(job: Job)
                -> Result<Artifact, Error>
                effects [io, time, network]
                requires job != empty
            {
                let src = await fetch(job.source)
                return Ok(Artifact(src))
            }
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var actor = json.RootElement.GetProperty("actors").EnumerateArray().Single();
        AssertTrue(actor.GetProperty("name").GetString() == "BuildWorker", "Actor name was not emitted.");
        AssertTrue(actor.GetProperty("runtimeProfile").GetString() == "actor", "Actor runtime profile was not emitted.");

        var state = actor.GetProperty("stateMembers").EnumerateArray().Single();
        AssertTrue(state.GetProperty("name").GetString() == "current", "Actor state name was not emitted.");
        AssertTrue(state.GetProperty("type").GetString() == "Option<Job>", "Actor state type was not emitted.");

        var handler = actor.GetProperty("handlers").EnumerateArray().Single();
        AssertTrue(handler.GetProperty("name").GetString() == "handle", "Actor handler name was not emitted.");
        AssertTrue(handler.GetProperty("isAsync").GetBoolean(), "Actor handler async flag was not emitted.");
        AssertTrue(handler.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()).SequenceEqual(["io", "time", "network"]), "Actor handler effects were not emitted.");
        AssertTrue(handler.GetProperty("body").GetProperty("statements").EnumerateArray().Count() == 2, "Actor handler body statements were not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsTypedFunctionBodyStatements()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-body-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose function body JSON"
        fn score(order: Order)
            -> Result<Score, Error>
            requires order != empty
        {
            let value: Score = calculate(order)
            return Ok(value)
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var statements = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("body")
            .GetProperty("statements")
            .EnumerateArray()
            .ToArray();

        AssertTrue(statements.Length == 2, "Expected two body statements in CLI JSON.");
        AssertTrue(statements[0].GetProperty("kind").GetString() == "let", "Expected let statement kind.");
        AssertTrue(statements[0].GetProperty("name").GetString() == "value", "Expected let binding name.");
        AssertTrue(statements[0].GetProperty("typeAnnotation").GetString() == "Score", "Expected let type annotation.");
        AssertTrue(statements[0].GetProperty("initializer").GetProperty("staticType").GetString() == "Score", "Expected typed initializer static type.");
        AssertTrue(statements[1].GetProperty("kind").GetString() == "return", "Expected return statement kind.");
        AssertTrue(statements[1].GetProperty("expression").GetProperty("staticType").GetString() == "Result<Score, Error>", "Expected return expression static type.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsModelCallChains()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-model-call-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose model call JSON"
        schema PlanDraft = {
            region: Region
        }
        fn choose_plan(order: Order)
            -> Result<Plan, Error>
            effects [model]
            requires order != empty
        {
            let response =
                model local "qwen3-coder"
                    .task("Produce a deployment plan under budget")
                    .input(order)
                    .output<PlanDraft>()
                    .replay(seed: 7, temperature: 0.0)
                    .capture_provenance()
                    .run()?
            return Ok(Plan())
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var modelCall = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("modelCalls")
            .EnumerateArray()
            .Single();

        AssertTrue(modelCall.GetProperty("provider").GetString() == "local", "Model call provider was not emitted.");
        AssertTrue(modelCall.GetProperty("model").GetString() == "qwen3-coder", "Model call id was not emitted.");
        AssertTrue(modelCall.GetProperty("outputSchema").GetString() == "PlanDraft", "Model call output schema was not emitted.");
        AssertTrue(modelCall.GetProperty("capturesProvenance").GetBoolean(), "Model call provenance flag was not emitted.");
        AssertTrue(modelCall.GetProperty("replay").GetProperty("arguments").GetString() == "seed: 7, temperature: 0.0", "Model call replay arguments were not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsProvenanceMetadata()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-provenance-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose provenance metadata"
        schema PatchPlan = {
            files: List<SourceFile>
        }
        fn propose_patch(change: ChangeRequest)
            -> Result<PatchPlan, Error>
            effects [model]
            requires change != empty
        {
            return model local "qwen3-coder"
                .task("Produce a reviewed patch plan")
                .input(change)
                .output<PatchPlan>()
                .replay(seed: 7, temperature: 0.0)
                .capture_provenance()
                .run()
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var modelCall = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("modelCalls")
            .EnumerateArray()
            .Single();

        var replay = modelCall.GetProperty("replay");
        AssertTrue(replay.GetProperty("seed").GetInt32() == 7, "Replay seed was not emitted as structured metadata.");
        AssertTrue(replay.GetProperty("temperature").GetDecimal() == 0.0m, "Replay temperature was not emitted as structured metadata.");

        var provenance = modelCall.GetProperty("provenance");
        AssertTrue(provenance.GetProperty("captureEnabled").GetBoolean(), "Provenance capture flag was not emitted.");
        AssertTrue(provenance.GetProperty("provider").GetString() == "local", "Provenance provider was not emitted.");
        AssertTrue(provenance.GetProperty("model").GetString() == "qwen3-coder", "Provenance model id was not emitted.");
        AssertTrue(provenance.GetProperty("replay").GetProperty("seed").GetInt32() == 7, "Provenance replay seed was not emitted.");
        AssertTrue(provenance.GetProperty("toolInvocations").GetArrayLength() == 0, "Provenance tool invocation list was not emitted.");
        AssertTrue(provenance.GetProperty("fileHashes").GetArrayLength() == 0, "Provenance file hash list was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsSchemaMetadataForModelOutputs()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-schema-metadata-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose schema metadata"
        schema PatchPlan = {
            files: List<SourceFile>,
            commands: List<ToolCommand>
        }
        schema ReviewNote = {
            message: Text
        }
        fn propose_patch(change: ChangeRequest)
            -> Result<PatchPlan, Error>
            effects [model]
            requires change != empty
        {
            return model local "qwen3-coder"
                .task("Produce a reviewed patch plan")
                .input(change)
                .output<PatchPlan>()
                .capture_provenance()
                .run()
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var schemas = json.RootElement.GetProperty("schemas").EnumerateArray().ToArray();
        var patchPlan = schemas.Single(schema => schema.GetProperty("name").GetString() == "PatchPlan");
        var patchMetadata = patchPlan.GetProperty("metadata");
        AssertTrue(patchMetadata.GetProperty("usedAsModelOutput").GetBoolean(), "Referenced schema should be marked as a model output.");
        AssertTrue(patchMetadata.GetProperty("modelOutputReferenceCount").GetInt32() == 1, "Referenced schema should report its model-output reference count.");

        var reference = patchMetadata.GetProperty("modelOutputReferences").EnumerateArray().Single();
        AssertTrue(reference.GetProperty("function").GetString() == "propose_patch", "Schema reference should include the referencing function.");
        AssertTrue(reference.GetProperty("provider").GetString() == "local", "Schema reference should include the model provider.");
        AssertTrue(reference.GetProperty("model").GetString() == "qwen3-coder", "Schema reference should include the model id.");
        AssertTrue(reference.GetProperty("task").GetString() == "Produce a reviewed patch plan", "Schema reference should include the model task.");
        AssertTrue(reference.GetProperty("location").GetProperty("line").GetInt32() == 17, "Schema reference should include the output location line.");

        var reviewNote = schemas.Single(schema => schema.GetProperty("name").GetString() == "ReviewNote");
        var reviewMetadata = reviewNote.GetProperty("metadata");
        AssertTrue(!reviewMetadata.GetProperty("usedAsModelOutput").GetBoolean(), "Unreferenced schema should not be marked as a model output.");
        AssertTrue(reviewMetadata.GetProperty("modelOutputReferenceCount").GetInt32() == 0, "Unreferenced schema should report zero model-output references.");
        AssertTrue(reviewMetadata.GetProperty("modelOutputReferences").GetArrayLength() == 0, "Unreferenced schema should emit an empty reference list.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsMetaprogrammingDeclarations()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-metaprogramming-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose metaprogramming JSON"
        macro derive_decoder(input: Ast.Record) -> Ast.Declaration
            input = typed_ast
            expands = declarations
        {
            return ast.declaration(input.name + "Decoder")
        }
        generator schema_manifest(module: Ast.Module) -> GeneratedFile
            incremental = true
            deterministic = true
            tracks = [schemas, tools]
            output = "generated/schema_manifest.ai"
        {
            return emit_manifest(module.schemas)
        }
        fn validate()
            -> Result<Unit, Error>
            requires true
        {
            return Ok(Unit())
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var macro = json.RootElement.GetProperty("macros").EnumerateArray().Single();
        AssertTrue(macro.GetProperty("name").GetString() == "derive_decoder", "Macro name was not emitted.");
        AssertTrue(macro.GetProperty("parameters").EnumerateArray().Single().GetProperty("type").GetString() == "Ast.Record", "Macro parameter type was not emitted.");
        AssertTrue(macro.GetProperty("returnType").GetString() == "Ast.Declaration", "Macro return type was not emitted.");
        AssertTrue(macro.GetProperty("input").GetString() == "typed_ast", "Macro input model was not emitted.");
        AssertTrue(macro.GetProperty("expands").GetString() == "declarations", "Macro expansion target was not emitted.");

        var gen = json.RootElement.GetProperty("generators").EnumerateArray().Single();
        AssertTrue(gen.GetProperty("name").GetString() == "schema_manifest", "Generator name was not emitted.");
        AssertTrue(gen.GetProperty("incremental").GetBoolean(), "Generator incremental flag was not emitted.");
        AssertTrue(gen.GetProperty("deterministic").GetBoolean(), "Generator deterministic flag was not emitted.");
        AssertTrue(gen.GetProperty("tracks").EnumerateArray().Select(track => track.GetString()).SequenceEqual(["schemas", "tools"]), "Generator tracked dependencies were not emitted.");
        AssertTrue(gen.GetProperty("output").GetString() == "generated/schema_manifest.ai", "Generator output path was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsConsistentCamelCaseModel()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-json-shape-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose consistent JSON shape"
        capability OrdersApi
        capability tmpfs
        resource ModelSession
        resource Secret<T>
        effect net
        type OrderStatus = Active | Archived
        type Order = {
            id: OrderId,
            status: OrderStatus
        }
        tool validate_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = [tmpfs]
            timeout = 30s
        fn summarize(session: owned ModelSession, key: borrowed Secret<ApiKey>, api: OrdersApi)
            -> Result<Summary, Error>
            effects [net]
            requires session != empty
        {
            return Err(MissingSummary)
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var type = json.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Order");
        AssertTrue(type.GetProperty("kind").GetString() == "record", "Type kind was not emitted with camel-case property names.");
        AssertTrue(type.GetProperty("cases").GetArrayLength() == 0, "Record cases should be emitted as an empty array.");
        var typeField = type.GetProperty("fields").EnumerateArray().First();
        AssertTrue(typeField.GetProperty("name").GetString() == "id", "Record field name was not emitted in camel-case JSON.");
        AssertTrue(typeField.GetProperty("type").GetString() == "OrderId", "Record field type was not emitted in camel-case JSON.");
        AssertTrue(typeField.GetProperty("ownership").GetString() == "unspecified", "Record fields should emit unspecified ownership.");

        var tool = json.RootElement.GetProperty("tools").EnumerateArray().Single();
        AssertTrue(tool.GetProperty("name").GetString() == "validate_patch", "Tool name was not emitted in camel-case JSON.");
        AssertTrue(tool.GetProperty("returnType").GetString() == "TestReport", "Tool returnType was not emitted in camel-case JSON.");
        AssertTrue(tool.GetProperty("sandbox").GetString() == "wasm", "Tool sandbox was not emitted in camel-case JSON.");
        AssertTrue(tool.GetProperty("capabilities").EnumerateArray().Single().GetString() == "tmpfs", "Tool capabilities were not emitted in camel-case JSON.");
        AssertTrue(tool.GetProperty("timeout").GetString() == "30s", "Tool timeout was not emitted in camel-case JSON.");
        var toolParameter = tool.GetProperty("parameters").EnumerateArray().Single();
        AssertTrue(toolParameter.GetProperty("name").GetString() == "patch", "Tool parameter name was not emitted in camel-case JSON.");
        AssertTrue(toolParameter.GetProperty("type").GetString() == "Patch", "Tool parameter type was not emitted in camel-case JSON.");
        AssertTrue(toolParameter.GetProperty("ownership").GetString() == "unspecified", "Tool parameter ownership was not emitted as a string.");

        var function = json.RootElement.GetProperty("functions").EnumerateArray().Single();
        AssertTrue(function.GetProperty("name").GetString() == "summarize", "Function name was not emitted in camel-case JSON.");
        AssertTrue(function.GetProperty("returnType").GetString() == "Result<Summary, Error>", "Function returnType was not emitted in camel-case JSON.");
        AssertTrue(function.GetProperty("effects").EnumerateArray().Single().GetString() == "net", "Function effects were not emitted in camel-case JSON.");
        var parameters = function.GetProperty("parameters").EnumerateArray().ToArray();
        AssertTrue(parameters[0].GetProperty("ownership").GetString() == "owned", "Owned function parameter ownership was not emitted as a string.");
        AssertTrue(parameters[1].GetProperty("ownership").GetString() == "borrowed", "Borrowed function parameter ownership was not emitted as a string.");
        AssertTrue(parameters[2].GetProperty("ownership").GetString() == "unspecified", "Unspecified function parameter ownership was not emitted as a string.");
        var contract = function.GetProperty("contracts").EnumerateArray().Single();
        AssertTrue(contract.GetProperty("kind").GetString() == "requires", "Contract kind was not emitted in lower-case JSON.");
        AssertTrue(contract.GetProperty("expression").GetString() == "session != empty", "Contract expression was not emitted in camel-case JSON.");

        AssertTrue(json.RootElement.GetProperty("diagnostics").GetArrayLength() == 0, "Expected no diagnostics for valid source.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsSourceMapFieldsWithStablePropertyCasing()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-json-source-map-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose source map JSON"
        policy Data {
            classification = internal
            inference = local_only
        }
        capability OrdersApi
        resource ModelSession
        effect net
        type Order = {
            id: OrderId
        }
        tool validate_patch(patch: Patch) -> TestReport
            sandbox = wasm
            capabilities = [OrdersApi]
            timeout = 30s
        fn summarize(session: owned ModelSession, api: OrdersApi)
            -> Result<Summary, Error>
            effects [net, network]
            requires session != empty
        {
            let response: Text = api.get("/orders")
            return Ok(Summary(response))
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        AssertCamelCasePropertyNames(json.RootElement, "$");

        var policyLocation = json.RootElement.GetProperty("policies")
            .EnumerateArray()
            .Single()
            .GetProperty("location");
        AssertJsonSourceLocation(policyLocation, sourcePath, expectedLine: 2, expectedColumn: 8);

        var typeLocation = json.RootElement.GetProperty("types")
            .EnumerateArray()
            .Single()
            .GetProperty("location");
        AssertJsonSourceLocation(typeLocation, sourcePath, expectedLine: 9, expectedColumn: 6);

        var toolLocation = json.RootElement.GetProperty("tools")
            .EnumerateArray()
            .Single()
            .GetProperty("location");
        AssertJsonSourceLocation(toolLocation, sourcePath, expectedLine: 12, expectedColumn: 6);

        var functionLocation = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("location");
        AssertJsonSourceLocation(functionLocation, sourcePath, expectedLine: 16, expectedColumn: 4);

        var contractLocation = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("contracts")
            .EnumerateArray()
            .Single()
            .GetProperty("location");
        AssertJsonSourceLocation(contractLocation, sourcePath, expectedLine: 19, expectedColumn: 5);

        var statementLocation = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("body")
            .GetProperty("statements")
            .EnumerateArray()
            .First()
            .GetProperty("location");
        AssertJsonSourceLocation(statementLocation, sourcePath, expectedLine: 21, expectedColumn: 5);
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsStructuredDiagnostics()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-json-diagnostics-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose diagnostic JSON"
        capability OrdersApi
        fn fetch(api: MissingApi)
            -> Result<Order, Error>
            requires api != empty
        {
            return Err(NotFound)
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 2, $"Expected CLI JSON command to return diagnostic exit code 2. Exit {process.ExitCode}. STDERR: {stderr}");
        AssertTrue(string.IsNullOrWhiteSpace(stderr), $"Expected JSON diagnostics on stdout without stderr noise. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        AssertTrue(json.RootElement.GetProperty("success").GetBoolean() == false, "Expected JSON success flag to be false.");

        var diagnostic = json.RootElement.GetProperty("diagnostics")
            .EnumerateArray()
            .Single(d => d.GetProperty("code").GetString() == "AI2008");

        AssertTrue(diagnostic.GetProperty("severity").GetString() == "error", "Diagnostic severity was not emitted in lower-case JSON.");
        AssertTrue(!string.IsNullOrWhiteSpace(diagnostic.GetProperty("message").GetString()), "Diagnostic message was not emitted.");

        var location = diagnostic.GetProperty("location");
        AssertTrue(location.GetProperty("sourcePath").GetString() == sourcePath, "Diagnostic location sourcePath was not emitted.");
        AssertTrue(location.GetProperty("line").GetInt32() == 3, "Diagnostic location line was not nested and emitted correctly.");
        AssertTrue(location.GetProperty("column").GetInt32() == 15, "Diagnostic location column was not nested and emitted correctly.");
        AssertTrue(location.GetProperty("offset").GetInt32() >= 0, "Diagnostic location offset was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsResolvedSymbolMetadata()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-json-symbols-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose resolved symbol JSON"
        capability Network
        resource Socket
        schema Reply = { message: string }
        tool fetch(url: string) -> Reply
            sandbox = wasm
            capabilities = [Network]
            timeout = 30s
        fn run(socket: Socket) -> Reply
            effects [network, model]
            requires socket != empty
        {
            return model local "qwen3-coder"
                .task("reply")
                .input(socket)
                .output<Reply>()
                .run()
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");
        AssertTrue(string.IsNullOrWhiteSpace(stderr), $"Expected JSON output without stderr noise. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("symbols").EnumerateArray().Any(s =>
            s.GetProperty("name").GetString() == "Network"
            && s.GetProperty("kind").GetString() == "Capability"), "Expected declaration symbol metadata for capability Network.");

        var references = root.GetProperty("resolvedSymbolReferences").EnumerateArray().ToArray();
        AssertTrue(references.Any(r =>
            r.GetProperty("name").GetString() == "Network"
            && r.GetProperty("referenceKind").GetString() == "ToolCapability"
            && r.GetProperty("resolvedKind").GetString() == "Capability"), "Expected resolved tool capability metadata.");
        AssertTrue(references.Any(r =>
            r.GetProperty("name").GetString() == "Socket"
            && r.GetProperty("referenceKind").GetString() == "Resource"
            && r.GetProperty("resolvedKind").GetString() == "Resource"), "Expected resolved resource parameter metadata.");
        AssertTrue(references.Any(r =>
            r.GetProperty("name").GetString() == "network"
            && r.GetProperty("referenceKind").GetString() == "Effect"
            && r.GetProperty("resolvedKind").GetString() == "Effect"), "Expected resolved function effect metadata.");
        AssertTrue(references.Any(r =>
            r.GetProperty("name").GetString() == "Reply"
            && r.GetProperty("referenceKind").GetString() == "ModelOutputSchema"
            && r.GetProperty("resolvedKind").GetString() == "Schema"), "Expected resolved model output schema metadata.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsFirstClassFunctionEffects()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-built-in-effects-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose built-in effect JSON"
        fn gather()
            -> Result<Report, Error>
            effects [io, network, unsafe_ffi]
            requires true
        {
            return Ok(Report())
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var documentEffects = json.RootElement.GetProperty("effects").EnumerateArray().ToArray();
        AssertTrue(documentEffects.Length == 0, "Built-in effects should not be emitted as document-scope declarations.");

        var functionEffects = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("effects")
            .EnumerateArray()
            .Select(effect => effect.GetString())
            .ToArray();

        AssertTrue(functionEffects.SequenceEqual(["io", "network", "unsafe_ffi"]), "First-class function effects were not emitted in CLI JSON.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsEffectCheckSummary()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-effect-summary-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose effect summary"
        fn fetchOrder(url: string) -> string
            effects [network]
            requires true
        {
            return http.get(url)
        }

        fn summarize(url: string) -> string
            effects [network]
            requires true
        {
            return fetchOrder(url)
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var summaries = json.RootElement.GetProperty("effectSummary")
            .EnumerateArray()
            .ToDictionary(summary => summary.GetProperty("functionName").GetString()!);

        var fetch = summaries["fetchOrder"];
        AssertTrue(fetch.GetProperty("declaredEffects").EnumerateArray().Select(effect => effect.GetString()).SequenceEqual(["network"]),
            "fetchOrder declared effect summary was not emitted.");
        AssertTrue(fetch.GetProperty("inferredRequiredEffects").EnumerateArray().Select(effect => effect.GetString()).SequenceEqual(["network"]),
            "fetchOrder inferred effect summary was not emitted.");
        AssertTrue(fetch.GetProperty("isCompliant").GetBoolean(), "fetchOrder should be effect-compliant.");

        var summarize = summaries["summarize"];
        AssertTrue(summarize.GetProperty("inferredRequiredEffects").EnumerateArray().Select(effect => effect.GetString()).SequenceEqual(["network"]),
            "summarize should report propagated network effect in CLI JSON.");
        AssertTrue(summarize.GetProperty("isCompliant").GetBoolean(), "summarize should be effect-compliant after declaring network.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonExitsNonzeroForEffectDiagnostics()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-effect-diagnostic-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Effect diagnostic exit code"
        schema Completion = { text: string }
        fn complete(prompt: string) -> string
            requires true
        {
            return model local "qwen3-coder"
                .input(prompt)
                .output<Completion>()
                .run()
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 2,
            $"Expected CLI to exit 2 for semantic effect diagnostics. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        AssertTrue(!json.RootElement.GetProperty("success").GetBoolean(), "CLI JSON success should be false for effect diagnostics.");
        AssertTrue(json.RootElement.GetProperty("semanticDiagnostics")
            .EnumerateArray()
            .Any(diagnostic => diagnostic.GetProperty("code").GetString() == "AI3030"),
            "CLI JSON should include AI3030 in semanticDiagnostics.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsContractVerificationHooks()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-contract-hooks-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose contract hooks"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires order != empty
            ensures result is Ok => result.value.total >= 0
            invariant result is Err || result.value.total >= 0
        {
            return Ok(Quote(0))
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var contracts = json.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Single()
            .GetProperty("contracts")
            .EnumerateArray()
            .ToArray();

        AssertTrue(contracts.Select(c => c.GetProperty("checkPhase").GetString()).SequenceEqual(["precondition", "postcondition", "invariant"]), "Contract check phases were not emitted in CLI JSON.");
        AssertTrue(contracts.All(c => c.GetProperty("emitRuntimeCheck").GetBoolean()), "Runtime check flags were not emitted in CLI JSON.");
        AssertTrue(contracts.All(c => c.GetProperty("emitDebugCheck").GetBoolean()), "Debug check flags were not emitted in CLI JSON.");
        AssertTrue(contracts.All(c => c.GetProperty("emitVerificationCondition").GetBoolean()), "Verification condition flags were not emitted in CLI JSON.");
        AssertTrue(contracts.Select(c => c.GetProperty("verificationHook").GetString()).SequenceEqual(["requires", "ensures", "invariant"]), "Verification hook names were not emitted in CLI JSON.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliJsonEmitsContractSemanticMetadata()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-contract-semantics-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Expose contract semantic metadata"
        fn estimate(order: Order)
            -> Result<Quote, Error>
            requires order != empty
            ensures result is Ok => result.value.total >= 0
        {
            return Ok(Quote(0))
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var json = JsonDocument.Parse(stdout);
        var summary = json.RootElement.GetProperty("contractSummary").EnumerateArray().ToArray();
        AssertTrue(summary.Length == 2, "Expected contract summary entries.");

        var requires = summary[0];
        AssertTrue(requires.GetProperty("isBoolean").GetBoolean(), "Contract Boolean result was not emitted.");
        AssertTrue(requires.GetProperty("availableNames").EnumerateArray().Select(name => name.GetString()).SequenceEqual(["order"]), "Requires available names were not emitted.");
        AssertTrue(requires.GetProperty("unknownNames").EnumerateArray().Count() == 0, "Expected no unknown contract names.");
        AssertTrue(requires.GetProperty("runtimeCheck").GetProperty("emit").GetBoolean(), "Runtime check metadata was not emitted.");
        AssertTrue(requires.GetProperty("runtimeCheck").GetProperty("phase").GetString() == "precondition", "Runtime check phase was not emitted.");
        AssertTrue(requires.GetProperty("verificationCondition").GetProperty("emit").GetBoolean(), "Verification condition metadata was not emitted.");
        AssertTrue(requires.GetProperty("verificationCondition").GetProperty("hook").GetString() == "requires", "Verification hook was not emitted.");
        AssertTrue(requires.GetProperty("verificationCondition").GetProperty("location").GetProperty("line").GetInt32() > 0, "Verification location was not emitted.");

        var ensuresNames = summary[1].GetProperty("availableNames").EnumerateArray().Select(name => name.GetString()).ToArray();
        AssertTrue(ensuresNames.SequenceEqual(["order", "result"]), "Ensures available names should include result.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliValidateCommandEmitsResultEnvelope()
{
    var sourcePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");

    var result = RunCli($"validate {Quote(sourcePath)} --json");

    AssertTrue(result.ExitCode == 0, $"Expected validate command to succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");

    using var json = JsonDocument.Parse(result.Stdout);
    var root = json.RootElement;
    AssertTrue(root.GetProperty("command").GetString() == "validate", "Validate JSON should include command.");
    AssertTrue(root.GetProperty("success").GetBoolean(), "Validate JSON success should be true.");
    AssertTrue(root.GetProperty("diagnostics").ValueKind == JsonValueKind.Array, "Validate JSON should include diagnostics array.");
    AssertTrue(root.GetProperty("artifacts").ValueKind == JsonValueKind.Array, "Validate JSON should include artifacts array.");
    AssertTrue(root.GetProperty("timing").GetProperty("elapsedMilliseconds").GetDouble() >= 0, "Validate JSON should include elapsed timing.");
}

static void CliValidateCommandAcceptsSingleSourceSampleDirectory()
{
    var sampleDir = Path.Combine(Path.GetTempPath(), $"aisharp-validate-dir-{Guid.NewGuid():N}");
    Directory.CreateDirectory(sampleDir);

    try
    {
        File.WriteAllText(Path.Combine(sampleDir, "workflow.aisharp"), """
            intent "Directory validation"
            fn identity(value: string) -> string
                requires value != empty
                ensures true
            {
                return value
            }
            """);

        var result = RunCli($"validate {Quote(sampleDir)} --json");

        AssertTrue(result.ExitCode == 0,
            $"Expected validate command to accept a directory with one .aisharp source. Exit {result.ExitCode}. STDOUT: {result.Stdout}\nSTDERR: {result.Stderr}");

        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("success").GetBoolean(), "Directory validate JSON success should be true.");
        var artifactPath = root.GetProperty("artifacts").EnumerateArray().Single().GetProperty("path").GetString();
        AssertTrue(string.Equals(Path.GetFullPath(Path.Combine(sampleDir, "workflow.aisharp")), artifactPath, StringComparison.OrdinalIgnoreCase),
            $"Validate artifact should point at the resolved .aisharp source. Got: {artifactPath}");
    }
    finally
    {
        if (Directory.Exists(sampleDir))
            Directory.Delete(sampleDir, recursive: true);
    }
}

static void CliLowerCommandWritesGeneratedCSharpArtifact()
{
    var sourcePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var outDir = Path.Combine(Path.GetTempPath(), $"aisharp-lower-{Guid.NewGuid():N}");

    try
    {
        var result = RunCli($"lower {Quote(sourcePath)} --out {Quote(outDir)} --json");

        AssertTrue(result.ExitCode == 0, $"Expected lower command to succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");
        var generatedFiles = Directory.GetFiles(outDir, "*.cs", SearchOption.TopDirectoryOnly);
        AssertTrue(generatedFiles.Length == 1, "Lower command should write one generated C# file.");
        AssertTrue(File.ReadAllText(generatedFiles[0]).Contains("Generated by AISharp compiler", StringComparison.Ordinal),
            "Generated C# artifact should contain AISharp compiler header.");

        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("command").GetString() == "lower", "Lower JSON should include command.");
        AssertTrue(root.GetProperty("success").GetBoolean(), "Lower JSON success should be true.");
        var artifact = root.GetProperty("artifacts")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("kind").GetString() == "csharp");
        AssertTrue(artifact.GetProperty("kind").GetString() == "csharp", "Lower JSON should report a C# artifact.");
        AssertTrue(File.Exists(artifact.GetProperty("path").GetString()), "Lower JSON artifact path should exist.");
        AssertTrue(root.GetProperty("timing").GetProperty("elapsedMilliseconds").GetDouble() >= 0, "Lower JSON should include elapsed timing.");
    }
    finally
    {
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
    }
}

static void CliBuildCommandCompilesSimpleSample()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-build-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "Buildable"
        type Point = { x: int, y: int }
        fn makePoint(x: int, y: int) -> Point
            requires true
        {
            return Point(x, y)
        }
        fn answer() -> int
            requires true
        {
            return 42
        }
        """);

    try
    {
        var result = RunCli($"build {Quote(sourcePath)} --json");

        AssertTrue(result.ExitCode == 0, $"Expected build command to succeed. Exit {result.ExitCode}. STDOUT: {result.Stdout} STDERR: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("command").GetString() == "build", "Build JSON should include command.");
        AssertTrue(root.GetProperty("success").GetBoolean(), "Build JSON success should be true.");
        AssertTrue(root.GetProperty("artifacts").EnumerateArray().Any(artifact => artifact.GetProperty("kind").GetString() == "assembly"),
            "Build JSON should include an assembly artifact.");
        AssertTrue(root.GetProperty("timing").GetProperty("elapsedMilliseconds").GetDouble() >= 0, "Build JSON should include elapsed timing.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliFormatCommandNormalizesSourceFile()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-format-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, "intent \"Format\"   \r\nfn ok() -> int   \r\n    requires true   \r\n{\r\n    return 1   \r\n}");

    try
    {
        var result = RunCli($"format {Quote(sourcePath)} --json");

        AssertTrue(result.ExitCode == 0, $"Expected format command to succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");
        AssertTrue(File.ReadAllText(sourcePath) == "intent \"Format\"\nfn ok() -> int\n    requires true\n{\n    return 1\n}\n",
            "Format command should trim trailing whitespace, normalize newlines, and end with one newline.");
        using var json = JsonDocument.Parse(result.Stdout);
        AssertTrue(json.RootElement.GetProperty("command").GetString() == "format", "Format JSON should include command.");
        AssertTrue(json.RootElement.GetProperty("artifacts").EnumerateArray().Single().GetProperty("kind").GetString() == "formatted-source",
            "Format JSON should report the formatted source artifact.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static string FormatterMessyRepresentativeSource() => """
    // file comment
       module   orders.service
    use   std.http

    policy CustomerPII {
    classification = secret
        inference = local_only // keep inline
    }
       capability OrdersApi
    resource   ModelSession
    effect json
    type OrderStatus = Active | Archived | Pending
    type Order = {
    id: OrderId,
      status: OrderStatus
    }
    schema OrderPatch = {
    customerId: CustomerId,
    status: OrderStatus
    }
    tool validate_patch(patch: Patch) -> TestReport
    sandbox = wasm
      capabilities = [tmpfs]
    timeout = 30s
    async fn handle(api: OrdersApi) -> Result<Order, OrderError>
    effects [network]
    requires true
    ensures result is Ok
    {
    let response = await api.get("/orders")
    match response {
    Active => Ok(response)
      _ => Err(NetworkFailure)
    }
    }
    actor Worker {
    state count: int
    async fn run(api: OrdersApi) -> int
    effects [network]
    requires true
    {
    taskgroup batch {
    spawn first = api.fetch()
    all first
    }
    return 1
    }
    }
    test "keeps format" {
    expect true == true
    }
    observe {
    counter "orders.count"
    }
    """;

static string FormatterRepresentativeExpected() => """
    // file comment
    module orders.service
    use std.http

    policy CustomerPII {
        classification = secret
        inference = local_only // keep inline
    }
    capability OrdersApi
    resource ModelSession
    effect json
    type OrderStatus = Active | Archived | Pending
    type Order = {
        id: OrderId,
        status: OrderStatus
    }
    schema OrderPatch = {
        customerId: CustomerId,
        status: OrderStatus
    }
    tool validate_patch(patch: Patch) -> TestReport
        sandbox = wasm
        capabilities = [tmpfs]
        timeout = 30s
    async fn handle(api: OrdersApi) -> Result<Order, OrderError>
        effects [network]
        requires true
        ensures result is Ok
    {
        let response = await api.get("/orders")
        match response {
            Active => Ok(response)
            _ => Err(NetworkFailure)
        }
    }
    actor Worker {
        state count: int
        async fn run(api: OrdersApi) -> int
            effects [network]
            requires true
        {
            taskgroup batch {
                spawn first = api.fetch()
                all first
            }
            return 1
        }
    }
    test "keeps format" {
        expect true == true
    }
    observe {
        counter "orders.count"
    }
    """;

static void FormatterFormatsRepresentativeConstructsIdempotently()
{
    var formatted = AISharpFormatter.Format(FormatterMessyRepresentativeSource());
    AssertEqual(FormatterRepresentativeExpected().Replace("\r\n", "\n", StringComparison.Ordinal), formatted);
    AssertEqual(formatted, AISharpFormatter.Format(formatted), "Formatting must be idempotent.");
}

static void FormatterOutputParsesSuccessfully()
{
    var formatted = AISharpFormatter.Format(FormatterMessyRepresentativeSource());
    var parse = AISharpParser.Parse(formatted);
    AssertTrue(parse.Success, "Parser should accept formatted representative source: " +
        string.Join("; ", parse.Diagnostics.Select(d => d.Message)));
}

static void CliFormatCheckReportsUnformattedFilesWithoutWriting()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-format-check-{Guid.NewGuid():N}.aisharp");
    var messy = "intent \"Format\"   \r\nfn ok() -> int\r\nrequires true\r\n{\r\nreturn 1\r\n}\r\n";
    File.WriteAllText(sourcePath, messy);

    try
    {
        var result = RunCli($"format {Quote(sourcePath)} --check --json");

        AssertTrue(result.ExitCode == 1, $"Expected --check to fail for unformatted file. STDOUT: {result.Stdout} STDERR: {result.Stderr}");
        AssertEqual(messy, File.ReadAllText(sourcePath), "--check must not write the source file.");
        using var json = JsonDocument.Parse(result.Stdout);
        AssertTrue(json.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d =>
            d.GetProperty("code").GetString() == "AI3001"), "Expected AI3001 formatting diagnostic.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliFormatStdoutPrintsFormattedOutputWithoutWriting()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-format-stdout-{Guid.NewGuid():N}.aisharp");
    var messy = "intent \"Format\"   \r\nfn ok() -> int\r\nrequires true\r\n{\r\nreturn 1\r\n}\r\n";
    File.WriteAllText(sourcePath, messy);

    try
    {
        var result = RunCli($"format {Quote(sourcePath)} --stdout");

        AssertTrue(result.ExitCode == 0, $"Expected --stdout to succeed. STDERR: {result.Stderr}");
        AssertEqual("intent \"Format\"\nfn ok() -> int\n    requires true\n{\n    return 1\n}\n", result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
        AssertEqual(messy, File.ReadAllText(sourcePath), "--stdout must not write the source file.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CliAuditCommandEmitsSpecProgressResult()
{
    var specPath = Path.Combine(Path.GetTempPath(), $"aisharp-audit-{Guid.NewGuid():N}.md");
    File.WriteAllText(specPath, """
        # Progress
        - [x] Parser complete
        - [ ] Runtime pending
        """);

    try
    {
        var result = RunCli($"audit {Quote(specPath)} --json");

        AssertTrue(result.ExitCode == 0, $"Expected audit command to succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("command").GetString() == "audit", "Audit JSON should include command.");
        AssertTrue(root.GetProperty("success").GetBoolean(), "Audit JSON success should be true.");
        var audit = root.GetProperty("audit");
        AssertTrue(audit.GetProperty("completedChecklistItems").GetInt32() == 1, "Audit completed count should be emitted.");
        AssertTrue(audit.GetProperty("incompleteChecklistItems").GetInt32() == 1, "Audit incomplete count should be emitted.");
        AssertTrue(audit.GetProperty("status").GetString() == "inProgress", "Audit status should use JSON enum casing.");
    }
    finally
    {
        File.Delete(specPath);
    }
}

static (int ExitCode, string Stdout, string Stderr) RunCli(string arguments)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"{CliCommandPrefix()} {arguments}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(ChildProcessTimeoutMilliseconds))
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        var timeoutStdout = stdoutTask.GetAwaiter().GetResult();
        var timeoutStderr = stderrTask.GetAwaiter().GetResult();
        return (-1, timeoutStdout, $"{timeoutStderr}{Environment.NewLine}Timed out after {ChildProcessTimeoutMilliseconds / 1000} seconds.");
    }

    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();

    return (process.ExitCode, stdout, stderr);
}

static string CliCommandPrefix()
{
    var configured = Environment.GetEnvironmentVariable("AISHARP_CLI_DLL");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        return "exec " + Quote(Path.GetFullPath(configured));
    return "run --project " + Quote(Path.Combine(FindRepositoryRoot(), "src", "AISharp.Cli", "AISharp.Cli.csproj")) + " --";
}

static string FindRepositoryRoot()
{
    var configured = Environment.GetEnvironmentVariable("AISHARP_REPOSITORY_ROOT");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "AISharp.sln")))
        return Path.GetFullPath(configured);
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AISharp.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not find repository root from test output directory.");
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

static void AssertCamelCasePropertyNames(JsonElement element, string path)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                AssertTrue(!string.IsNullOrWhiteSpace(property.Name), $"JSON property at {path} must not be empty.");
                AssertTrue(char.IsLower(property.Name[0]), $"JSON property '{path}.{property.Name}' should start with a lower-case character.");
                AssertCamelCasePropertyNames(property.Value, $"{path}.{property.Name}");
            }

            break;
        case JsonValueKind.Array:
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                AssertCamelCasePropertyNames(item, $"{path}[{index}]");
                index++;
            }

            break;
    }
}

static void AssertJsonSourceLocation(JsonElement location, string sourcePath, int expectedLine, int expectedColumn)
{
    AssertTrue(location.GetProperty("sourcePath").GetString() == sourcePath, "Location sourcePath should preserve the CLI input path.");
    AssertTrue(location.GetProperty("line").GetInt32() == expectedLine, $"Location line was not emitted correctly. Expected {expectedLine}, got {location.GetProperty("line").GetInt32()}.");
    AssertTrue(location.GetProperty("column").GetInt32() == expectedColumn, $"Location column was not emitted correctly. Expected {expectedColumn}, got {location.GetProperty("column").GetInt32()}.");
    AssertTrue(location.GetProperty("offset").GetInt32() >= 0, "Location offset should be emitted as a zero-based source offset.");
}

static void PackageManifestParsesValidManifest()
{
    const string toml = """
        [package]
        name = "orders-service"
        version = "1.0.0"
        target = "net9.0"
        sources = ["*.aisharp", "src/**/*.aisharp"]

        [policy]
        default = "CustomerPII"

        [dependencies]
        std = "1.0.0"
        ai.core = "0.2.3"

        [build]
        output = "artifacts"
        generate_csharp = true
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Success, $"Expected parse to succeed. Errors: {string.Join(", ", result.Diagnostics.Where(d => d.Severity == ManifestDiagnosticSeverity.Error).Select(d => d.Message))}");
    AssertTrue(result.Manifest is not null, "Expected non-null manifest.");
    AssertTrue(result.Manifest!.Package.Name == "orders-service", "Package name should be parsed.");
    AssertTrue(result.Manifest.Package.Version == "1.0.0", "Package version should be parsed.");
    AssertTrue(result.Manifest.Package.TargetFramework == "net9.0", "Target framework should be parsed.");
    AssertTrue(result.Manifest.Package.Sources.Count == 2, "Sources array should have 2 entries.");
    AssertTrue(result.Manifest.DefaultPolicy == "CustomerPII", "Default policy should be parsed from [policy] section.");
    AssertTrue(result.Manifest.Dependencies.ContainsKey("std"), "Dependencies should include 'std'.");
    AssertTrue(result.Manifest.Dependencies["ai.core"] == "0.2.3", "Dependency version should be parsed.");
    AssertTrue(result.Manifest.Build.OutputDirectory == "artifacts", "Build output should be parsed.");
    AssertTrue(result.Manifest.Build.GenerateCSharp, "GenerateCSharp should default to true.");
}

static void PackageManifestRequiresNameField()
{
    const string toml = """
        [package]
        version = "1.0.0"
        """;

    var result = ManifestParser.Parse(toml);

    AssertFalse(result.Success, "Expected parse to fail when name is missing.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "PM0011"), "Expected PM0011 diagnostic for missing name.");
}

static void PackageManifestRequiresVersionField()
{
    const string toml = """
        [package]
        name = "my-pkg"
        """;

    var result = ManifestParser.Parse(toml);

    AssertFalse(result.Success, "Expected parse to fail when version is missing.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "PM0012"), "Expected PM0012 diagnostic for missing version.");
}

static void PackageManifestRequiresPackageSection()
{
    const string toml = """
        [dependencies]
        std = "1.0.0"
        """;

    var result = ManifestParser.Parse(toml);

    AssertFalse(result.Success, "Expected parse to fail when [package] section is absent.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "PM0010"), "Expected PM0010 diagnostic for missing [package] section.");
}

static void PackageManifestRejectsInvalidPackageName()
{
    const string toml = """
        [package]
        name = "my package name"
        version = "1.0.0"
        """;

    var result = ManifestParser.Parse(toml);

    AssertFalse(result.Success, "Expected parse to fail for package name with spaces.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == "PM0013"), "Expected PM0013 diagnostic for invalid name.");
}

static void PackageManifestWarnsOnDuplicateKeys()
{
    const string toml = """
        [package]
        name = "my-pkg"
        name = "other"
        version = "1.0.0"
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Diagnostics.Any(d => d.Code == "PM0003" && d.Severity == ManifestDiagnosticSeverity.Warning),
        "Expected PM0003 warning diagnostic for duplicate key.");
}

static void PackageManifestParsesInlineArraySources()
{
    const string toml = """
        [package]
        name = "my-pkg"
        version = "1.0.0"
        sources = ["src/main.aisharp", "src/lib.aisharp", "tests/**/*.aisharp"]
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Success, "Expected parse to succeed.");
    AssertTrue(result.Manifest!.Package.Sources.Count == 3, "Expected 3 source entries.");
    AssertTrue(result.Manifest.Package.Sources[1] == "src/lib.aisharp", "Second source should be parsed correctly.");
}

static void PackageManifestParsesDependenciesSection()
{
    const string toml = """
        [package]
        name = "svc"
        version = "0.1.0"

        [dependencies]
        std.collections = "1.2.3"
        std.http = "2.0.0-beta"
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Success, "Expected parse to succeed.");
    AssertTrue(result.Manifest!.Dependencies.Count == 2, "Expected 2 dependencies.");
    AssertTrue(result.Manifest.Dependencies["std.http"] == "2.0.0-beta", "Dependency with pre-release version should parse.");
}

static void PackageManifestParsesBuildSection()
{
    const string toml = """
        [package]
        name = "svc"
        version = "1.0.0"

        [build]
        output = "dist"
        generate_csharp = false
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Success, "Expected parse to succeed.");
    AssertTrue(result.Manifest!.Build.OutputDirectory == "dist", "Build output should be 'dist'.");
    AssertFalse(result.Manifest.Build.GenerateCSharp, "GenerateCSharp should be false.");
}

static void PackageManifestParsesPolicySection()
{
    const string toml = """
        [package]
        name = "billing-svc"
        version = "1.0.0"

        [policy]
        default = "StrictPolicy"
        """;

    var result = ManifestParser.Parse(toml);

    AssertTrue(result.Success, "Expected parse to succeed.");
    AssertTrue(result.Manifest!.DefaultPolicy == "StrictPolicy", "Default policy should be 'StrictPolicy'.");
}

static void PackageLockfileRoundTrips()
{
    const string toml = """
        [package]
        name = "svc"
        version = "1.0.0"

        [dependencies]
        std = "1.0.0"
        """;

    var manifestResult = ManifestParser.Parse(toml);
    AssertTrue(manifestResult.Success, "Manifest parse should succeed for lockfile test.");

    var entries = new[]
    {
        new LockfileEntry("std", "1.0.0", null, "sha256:abc123"),
    };

    var lockfile = PackageLockfile.Create(manifestResult.Manifest!, entries);

    AssertTrue(lockfile.PackageName == "svc", "Lockfile should carry package name.");
    AssertTrue(lockfile.PackageVersion == "1.0.0", "Lockfile should carry package version.");
    AssertTrue(lockfile.LockfileVersion == PackageLockfile.CurrentVersion, "Lockfile version should be current.");
    AssertTrue(lockfile.Dependencies.Count == 1, "Lockfile should have 1 dependency.");
    AssertTrue(lockfile.Dependencies[0].IntegrityHash == "sha256:abc123", "Integrity hash should round-trip.");

    var json = lockfile.Serialize();
    var restored = PackageLockfile.Deserialize(json);

    AssertTrue(restored is not null, "Deserialized lockfile should not be null.");
    AssertTrue(restored!.PackageName == lockfile.PackageName, "Package name should round-trip through JSON.");
    AssertTrue(restored.Dependencies[0].ResolvedVersion == "1.0.0", "Dependency version should round-trip.");
}

static void PackageManifestLocatorFindsNearestManifest()
{
    var dir = Path.Combine(Path.GetTempPath(), $"aisharp-loc-{Guid.NewGuid():N}");
    var subDir = Path.Combine(dir, "src", "mymodule");
    Directory.CreateDirectory(subDir);
    var manifestPath = Path.Combine(dir, "aisharp.toml");

    File.WriteAllText(manifestPath, """
        [package]
        name = "locator-test"
        version = "1.0.0"
        """);

    try
    {
        // Should find the manifest by walking up from subDir.
        var found = ManifestLocator.FindManifest(subDir);
        AssertTrue(found is not null, "ManifestLocator should find manifest by walking up the tree.");
        AssertTrue(string.Equals(Path.GetFullPath(found!), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase),
            $"Found manifest path should match expected. Got: {found}");

        // Load and parse via locator.
        var loadResult = ManifestLocator.LoadManifest(found!);
        AssertTrue(loadResult.Success, "LoadManifest should parse the located file successfully.");
        AssertTrue(loadResult.Manifest!.Package.Name == "locator-test", "Loaded manifest should have correct name.");
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

static void NuGetPackagesProducedForAllLibraryProjects()
{
    var repoRoot = FindRepositoryRoot();

    // Check that artifact packages directory exists and contains expected packages.
    // Run dotnet pack for each packable project to ensure packages are produced fresh.
    var packDir = Path.Combine(repoRoot, "artifacts", "packages");
    Directory.CreateDirectory(packDir);

    foreach (var projectRelativePath in PackableProjectRelativePaths())
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"pack {Quote(Path.Combine(repoRoot, projectRelativePath))} -c Release --no-build --output {Quote(packDir)} --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet pack.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0,
            $"dotnet pack should succeed for {projectRelativePath}. Exit {process.ExitCode}. STDERR: {stderr}\nSTDOUT: {stdout}");
    }

    var expectedPackages = new[]
    {
        "AISharp.Compiler",
        "AISharp.Lowering",
        "AISharp.Runtime",
        "AISharp.LanguageServer",
        "AISharp.Package",
        "AISharp.Cli",
    };

    foreach (var pkg in expectedPackages)
    {
        var matches = Directory.GetFiles(packDir, $"{pkg}.*.nupkg");
        AssertTrue(matches.Length > 0, $"Expected NuGet package for {pkg} to be produced in {packDir}.");
    }
}

static IReadOnlyList<string> PackableProjectRelativePaths() =>
[
    Path.Combine("src", "AISharp.Compiler", "AISharp.Compiler.csproj"),
    Path.Combine("src", "AISharp.Lowering", "AISharp.Lowering.csproj"),
    Path.Combine("src", "AISharp.Runtime", "AISharp.Runtime.csproj"),
    Path.Combine("src", "AISharp.LanguageServer", "AISharp.LanguageServer.csproj"),
    Path.Combine("src", "AISharp.Package", "AISharp.Package.csproj"),
    Path.Combine("src", "AISharp.Cli", "AISharp.Cli.csproj"),
];

static void ReleaseHashManifestHashesArtifactFiles()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-rel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    var file1 = Path.Combine(tmpDir, "pkg1.nupkg");
    var file2 = Path.Combine(tmpDir, "pkg2.nupkg");
    File.WriteAllText(file1, "package1 contents");
    File.WriteAllText(file2, "package2 contents");

    try
    {
        var manifest = ReleaseHashManifest.Create("my-pkg", "1.0.0", tmpDir, [file1, file2]);

        AssertTrue(manifest.PackageName == "my-pkg", "Package name should be stored.");
        AssertTrue(manifest.PackageVersion == "1.0.0", "Package version should be stored.");
        AssertTrue(manifest.Entries.Count == 2, "Should hash 2 artifacts.");
        AssertTrue(manifest.Entries.All(e => e.Algorithm == "sha256"), "All entries should use sha256.");
        AssertTrue(manifest.Entries.All(e => e.Hash.Length == 64), "SHA-256 hex should be 64 chars.");
        AssertTrue(manifest.Entries.All(e => e.SizeBytes > 0), "Size should be positive.");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

static void ReleaseHashManifestRoundTrips()
{
    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-rel2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    var file = Path.Combine(tmpDir, "artifact.nupkg");
    File.WriteAllText(file, "data");

    try
    {
        var manifest = ReleaseHashManifest.Create("pkg", "0.1.0", tmpDir, [file]);
        var json = manifest.Serialize();
        var restored = ReleaseHashManifest.Deserialize(json);

        AssertTrue(restored is not null, "Deserialized manifest should not be null.");
        AssertTrue(restored!.PackageName == manifest.PackageName, "PackageName should round-trip.");
        AssertTrue(restored.Entries[0].Hash == manifest.Entries[0].Hash, "Hash should round-trip.");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

static void ReleaseSbomMinimalContainsAllComponents()
{
    var sbom = Releasesbom.CreateMinimal("aisharp", "0.1.0-preview");

    AssertTrue(sbom.SbomFormat == Releasesbom.AiSharpMinimalFormat, "SBOM format should be marked correctly.");
    AssertTrue(sbom.Components.Count == 6, "Minimal SBOM should list 6 components.");
    AssertTrue(sbom.Components.Any(c => c.Name == "AISharp.Runtime"), "SBOM should include AISharp.Runtime.");
    AssertTrue(sbom.Components.Any(c => c.Name == "AISharp.Cli"), "SBOM should include AISharp.Cli.");
    AssertTrue(sbom.Components.All(c => c.LicenseExpression == "Apache-2.0"), "All first-party components should be Apache-2.0.");
}

static void ReleaseSbomRoundTrips()
{
    var sbom = Releasesbom.CreateMinimal("aisharp", "0.1.0-preview");
    var json = sbom.Serialize();
    var restored = Releasesbom.Deserialize(json);

    AssertTrue(restored is not null, "Deserialized SBOM should not be null.");
    AssertTrue(restored!.PackageName == sbom.PackageName, "PackageName should round-trip.");
    AssertTrue(restored.Components.Count == sbom.Components.Count, "Components count should round-trip.");
}

static void ReleaseProvenanceRecordSerializesGates()
{
    var gates = new[]
    {
        new ReleaseGateResult("build", true, null),
        new ReleaseGateResult("tests", true, "293 passed"),
        new ReleaseGateResult("pack", true, null),
    };

    var record = new ReleaseProvenanceRecord(
        PackageName: "aisharp",
        PackageVersion: "0.1.0-preview",
        SourceCommit: "abc123",
        BuildCommand: "dotnet pack AISharp.sln -c Release",
        GeneratedAt: DateTimeOffset.UtcNow,
        PackagePaths: ["artifacts/packages/AISharp.Runtime.0.1.0-preview.nupkg"],
        HashManifestPath: "artifacts/release-hashes.json",
        SbomPath: "artifacts/sbom.json",
        SignaturePath: null,
        IsDryRun: true,
        Gates: gates);

    AssertTrue(record.AllGatesPassed, "All gates should pass.");

    var json = record.Serialize();
    var restored = ReleaseProvenanceRecord.Deserialize(json);

    AssertTrue(restored is not null, "Deserialized record should not be null.");
    AssertTrue(restored!.Gates.Count == 3, "Gates should round-trip.");
    AssertTrue(restored.AllGatesPassed, "AllGatesPassed should be true after round-trip.");
    AssertTrue(restored.IsDryRun, "IsDryRun should round-trip.");
}

static void ReleaseSigningDryRunWithNoProviderEmitsWarning()
{
    var result = ReleaseSigningService.DryRun(SigningConfiguration.DryRunOnly, []);

    AssertTrue(result.IsDryRun, "Result should be marked as dry-run.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == ReleaseSigningService.DiagMissingConfig
        && d.Severity == SigningDiagnosticSeverity.Warning),
        "Missing config warning should be emitted.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == ReleaseSigningService.DiagDryRunNote),
        "Dry-run note should be emitted.");
}

static void ReleaseSigningDryRunValidatesArtifactExistence()
{
    var config = new SigningConfiguration(SigningProviderKind.External, "azure-key-vault", true);
    var result = ReleaseSigningService.DryRun(config, ["nonexistent-artifact.nupkg"]);

    AssertFalse(result.Success, "Dry-run with missing artifact should fail.");
    AssertTrue(result.Diagnostics.Any(d => d.Code == ReleaseSigningService.DiagMissingArtifact
        && d.Severity == SigningDiagnosticSeverity.Error),
        "Missing artifact error should be emitted.");
}

static void ReleaseSigningDryRunSucceedsWithExistingArtifacts()
{
    var tmpFile = Path.Combine(Path.GetTempPath(), $"aisharp-sign-{Guid.NewGuid():N}.nupkg");
    File.WriteAllText(tmpFile, "fake package");

    try
    {
        var config = SigningConfiguration.DryRunOnly;
        var result = ReleaseSigningService.DryRun(config, [tmpFile]);

        AssertTrue(result.Success, "Dry-run with existing artifact should succeed.");
        AssertFalse(result.Diagnostics.Any(d => d.Severity == SigningDiagnosticSeverity.Error),
            "No errors should be emitted for existing artifacts.");
    }
    finally
    {
        File.Delete(tmpFile);
    }
}

static void SigningConfigurationRejectsSecretLikeFields()
{
    var json = "{\"provider\":\"external\",\"externalProviderHint\":\"azure-key-vault\",\"privateKey\":\"DO-NOT-COMMIT\"}";

    var threw = false;
    try
    {
        SigningConfiguration.LoadFromJson(json);
    }
    catch (InvalidOperationException ex)
    {
        threw = true;
        AssertTrue(ex.Message.Contains("privateKey", StringComparison.OrdinalIgnoreCase),
            "Error should name the offending secret-like field.");
    }

    AssertTrue(threw, "Loading a configuration containing a secret-like field should throw.");
}

static void SigningConfigurationLoadsExternalProviderHintFromJson()
{
    var json = "{\"provider\":\"external\",\"externalProviderHint\":\"azure-key-vault\",\"dryRun\":true,\"signatureOutputPath\":\"out/signatures.json\"}";

    var config = SigningConfiguration.LoadFromJson(json);

    AssertTrue(config.Provider == SigningProviderKind.External, "Provider should be External.");
    AssertTrue(config.ExternalProviderHint == "azure-key-vault", "Provider hint should round-trip.");
    AssertTrue(config.DryRun, "DryRun flag should round-trip.");
    AssertTrue(config.SignatureOutputPath == "out/signatures.json", "Signature output path should round-trip.");
}

static void SigningConfigurationMissingFileReturnsNull()
{
    var path = Path.Combine(Path.GetTempPath(), $"aisharp-sign-missing-{Guid.NewGuid():N}.json");
    var config = SigningConfiguration.LoadFromFile(path);

    AssertTrue(config is null, "Loading from a missing file should return null so the caller can render a diagnostic.");
}

static void ReleaseProvenanceCarriesSignaturePathAndDryRunState()
{
    var signedProvenance = new ReleaseProvenanceRecord(
        PackageName: "aisharp",
        PackageVersion: "0.1.0-preview",
        SourceCommit: "abc",
        BuildCommand: "dotnet pack",
        GeneratedAt: DateTimeOffset.UtcNow,
        PackagePaths: ["pkg1.nupkg"],
        HashManifestPath: "hashes.json",
        SbomPath: "sbom.json",
        SignaturePath: "signatures/pkg1.nupkg.sig",
        IsDryRun: false,
        Gates: [new ReleaseGateResult("signing", true, "signed")]);

    var dryRunProvenance = new ReleaseProvenanceRecord(
        PackageName: "aisharp",
        PackageVersion: "0.1.0-preview",
        SourceCommit: "abc",
        BuildCommand: "dotnet pack",
        GeneratedAt: DateTimeOffset.UtcNow,
        PackagePaths: ["pkg1.nupkg"],
        HashManifestPath: "hashes.json",
        SbomPath: "sbom.json",
        SignaturePath: null,
        IsDryRun: true,
        Gates: [new ReleaseGateResult("signing", true, "dry-run")]);

    var signedJson = signedProvenance.Serialize();
    var dryRunJson = dryRunProvenance.Serialize();

    AssertTrue(signedJson.Contains("signaturePath", StringComparison.Ordinal),
        "Signed provenance should record signaturePath.");
    AssertTrue(signedJson.Contains("signatures/pkg1.nupkg.sig", StringComparison.Ordinal),
        "Signed provenance should record the signature artifact path value.");
    AssertFalse(dryRunJson.Contains("signaturePath", StringComparison.Ordinal),
        "Dry-run provenance without a signature should omit signaturePath.");
    AssertTrue(dryRunJson.Contains("\"isDryRun\": true", StringComparison.Ordinal),
        "Dry-run provenance should record the dry-run flag.");

    var roundTrip = ReleaseProvenanceRecord.Deserialize(signedJson)!;
    AssertTrue(roundTrip.SignaturePath == "signatures/pkg1.nupkg.sig",
        "Signature path should round-trip through JSON.");
}

static void CliSignDryRunEmitsWarningWhenNoConfigurationProvided()
{
    var tmpFile = Path.Combine(Path.GetTempPath(), $"aisharp-sign-cli-{Guid.NewGuid():N}.nupkg");
    File.WriteAllText(tmpFile, "fake package");

    try
    {
        var result = RunCli($"sign --dry-run --artifact {Quote(tmpFile)} --json");

        AssertTrue(result.ExitCode == 0, $"Sign dry-run should succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.GetProperty("command").GetString() == "sign", "Command should be 'sign'.");
        AssertTrue(root.GetProperty("success").GetBoolean(), "Sign dry-run with existing artifact should succeed.");
        var sign = root.GetProperty("sign");
        AssertTrue(sign.GetProperty("isDryRun").GetBoolean(), "isDryRun should be true.");
        AssertTrue(sign.GetProperty("provider").GetString() == "none",
            "Provider should default to none when no config is provided.");

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        AssertTrue(diagnostics.Any(d => d.GetProperty("code").GetString() == ReleaseSigningService.DiagMissingConfig
            && d.GetProperty("severity").GetString() == "warning"),
            "Missing-config warning should be emitted.");
    }
    finally
    {
        File.Delete(tmpFile);
    }
}

static void CliSignDryRunValidatesArtifactList()
{
    var result = RunCli("sign --dry-run --artifact does-not-exist.nupkg --json");

    using var json = JsonDocument.Parse(result.Stdout);
    var root = json.RootElement;
    AssertFalse(root.GetProperty("success").GetBoolean(),
        "Sign dry-run with missing artifacts should not report success.");
    var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
    AssertTrue(diagnostics.Any(d => d.GetProperty("code").GetString() == ReleaseSigningService.DiagMissingArtifact
        && d.GetProperty("severity").GetString() == "error"),
        "Missing artifact error should be emitted.");
    AssertTrue(result.ExitCode != 0, "Sign dry-run with missing artifacts should return a non-zero exit code.");
}

static void CliSignReportsClearDiagnosticForMissingConfigurationFile()
{
    var missingConfig = Path.Combine(Path.GetTempPath(), $"aisharp-missing-config-{Guid.NewGuid():N}.json");
    var tmpFile = Path.Combine(Path.GetTempPath(), $"aisharp-sign-cli-{Guid.NewGuid():N}.nupkg");
    File.WriteAllText(tmpFile, "fake package");

    try
    {
        var result = RunCli($"sign --dry-run --config {Quote(missingConfig)} --artifact {Quote(tmpFile)} --json");

        AssertTrue(result.ExitCode != 0, "Missing config should produce non-zero exit code.");
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertFalse(root.GetProperty("success").GetBoolean(), "Missing config should fail the command.");
        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        AssertTrue(diagnostics.Any(d => d.GetProperty("code").GetString() == ReleaseSigningService.DiagMissingConfig
            && d.GetProperty("severity").GetString() == "error"
            && (d.GetProperty("message").GetString() ?? string.Empty).Contains(missingConfig, StringComparison.Ordinal)),
            "Diagnostic should clearly name the missing configuration path.");
    }
    finally
    {
        File.Delete(tmpFile);
    }
}

static void CliSignRejectsRealSigningWithoutDryRun()
{
    var result = RunCli("sign --json");

    AssertTrue(result.ExitCode != 0, "Sign without --dry-run should fail because real signing is not implemented.");
    using var json = JsonDocument.Parse(result.Stdout);
    var root = json.RootElement;
    AssertFalse(root.GetProperty("success").GetBoolean(), "Sign without --dry-run should report failure.");
    var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
    AssertTrue(diagnostics.Any(d => (d.GetProperty("message").GetString() ?? string.Empty)
        .Contains("dry-run", StringComparison.OrdinalIgnoreCase)),
        "Diagnostic should direct the user to use --dry-run.");
}

static void CliReleaseDryRunEmitsGatesAndPackages()
{
    var result = RunCli("release --dry-run --json");

    AssertTrue(result.ExitCode == 0, $"Expected release --dry-run to succeed. Exit {result.ExitCode}. STDERR: {result.Stderr}");
    using var json = JsonDocument.Parse(result.Stdout);
    var root = json.RootElement;
    AssertTrue(root.GetProperty("command").GetString() == "release", "Command should be 'release'.");
    AssertTrue(root.GetProperty("success").GetBoolean(), "Release dry-run should succeed.");

    var release = root.GetProperty("release");
    AssertTrue(release.GetProperty("isDryRun").GetBoolean(), "isDryRun should be true.");
    var gates = release.GetProperty("gates").EnumerateArray().ToList();
    AssertTrue(gates.Count >= 4, $"Expected at least 4 release gates. Got {gates.Count}.");
    AssertTrue(gates.All(g => g.GetProperty("passed").GetBoolean()),
        $"All gates should pass. Failed: {string.Join(", ", gates.Where(g => !g.GetProperty("passed").GetBoolean()).Select(g => g.GetProperty("name").GetString()))}");
}

static void CliAcceptanceCommandEmitsAllGates()
{
    var result = RunCli("acceptance --json");

    using var json = JsonDocument.Parse(result.Stdout);
    var root = json.RootElement;
    AssertTrue(root.GetProperty("command").GetString() == "acceptance", "Command should be 'acceptance'.");

    var acceptance = root.GetProperty("acceptance");
    var gates = acceptance.GetProperty("gates").EnumerateArray().ToList();

    var expectedGateNames = new[]
    {
        "build",
        "tests",
        "cli-sample-validate",
        "service-library-build",
        "ai-patch-sample",
        "actor-sample-validate",
        "release-dry-run"
    };
    AssertTrue(gates.Count >= expectedGateNames.Length,
        $"Expected at least {expectedGateNames.Length} acceptance gates. Got {gates.Count}.");

    var actualNames = gates.Select(g => g.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
    foreach (var expected in expectedGateNames)
    {
        AssertTrue(actualNames.Contains(expected),
            $"Acceptance summary missing gate '{expected}'. Got: {string.Join(", ", actualNames)}.");
    }

    foreach (var gate in gates)
    {
        AssertTrue(gate.TryGetProperty("passed", out _), "Each gate must include a 'passed' field.");
        AssertTrue(gate.TryGetProperty("logPath", out _), "Each gate must include a 'logPath' field.");
        if (!gate.GetProperty("passed").GetBoolean())
        {
            var detail = gate.GetProperty("detail").GetString();
            AssertTrue(!string.IsNullOrWhiteSpace(detail),
                $"Failed gate '{gate.GetProperty("name").GetString()}' must include a detail message.");
            var logPath = gate.GetProperty("logPath").GetString();
            AssertTrue(!string.IsNullOrWhiteSpace(logPath),
                $"Failed gate '{gate.GetProperty("name").GetString()}' must point at a log file.");
        }
    }

    AssertTrue(acceptance.TryGetProperty("logDirectory", out var logDirElement)
        && !string.IsNullOrWhiteSpace(logDirElement.GetString()),
        "Acceptance summary must include a logDirectory field.");
    AssertTrue(acceptance.TryGetProperty("summaryPath", out var summaryPathElement)
        && !string.IsNullOrWhiteSpace(summaryPathElement.GetString()),
        "Acceptance summary must include a summaryPath field.");

    var failed = acceptance.GetProperty("failed").GetInt32();
    var success = root.GetProperty("success").GetBoolean();
    if (failed == 0)
    {
        AssertTrue(success, "If no gates failed, acceptance.success must be true.");
        AssertTrue(result.ExitCode == 0, $"Expected acceptance exit 0 when all gates pass. Exit {result.ExitCode}.");
    }
    else
    {
        AssertFalse(success, "If any gate failed, acceptance.success must be false.");
        AssertTrue(result.ExitCode != 0, "Failed acceptance must surface a non-zero exit code.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertEqual<T>(T expected, T actual, string? message = null)
{
    if (!Equals(expected, actual))
    {
        throw new InvalidOperationException(
            message ?? $"Expected [{expected}] but got [{actual}].");
    }
}

static string? ReadStringProperty(object instance, string propertyName)
{
    var property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"Expected property '{propertyName}' to exist on {instance.GetType().Name}.");
    return property.GetValue(instance) as string;
}

static int ReadIntProperty(object instance, string propertyName)
{
    var property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"Expected property '{propertyName}' to exist on {instance.GetType().Name}.");
    return property.GetValue(instance) is int value
        ? value
        : throw new InvalidOperationException($"Expected property '{propertyName}' on {instance.GetType().Name} to be an int.");
}

static void BuildsSymbolTableForValidDocument()
{
    const string source = """
        intent "Build symbol table"
        policy DataPolicy {
            classification = internal
            inference = local_only
        }
        capability OrdersApi
        resource ModelSession
        effect net
        type Order = East | West
        schema OrderSchema = { id: string }
        tool validate_patch(patch: string) -> string
            sandbox = wasm
            capabilities = [OrdersApi]
            timeout = 5s
        fn process(api: OrdersApi) -> Result<string, Error>
            effects [net]
            requires api != empty
        {
            return Ok("")
        }
        actor BuildWorker {
            state current: string
            async fn handle(job: string) -> Result<string, Error>
                effects [net]
                requires job != empty
            {
                return Ok(job)
            }
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    AssertTrue(semanticResult.ParseResult == parseResult, "SemanticResult must carry the original ParseResult.");

    var table = semanticResult.SymbolTable ?? throw new InvalidOperationException("SymbolTable must not be null.");
    AssertTrue(table.LookupDocument(SymbolKind.Policy, "DataPolicy") != null, "Policy 'DataPolicy' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Capability, "OrdersApi") != null, "Capability 'OrdersApi' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Resource, "ModelSession") != null, "Resource 'ModelSession' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Effect, "net") != null, "Effect 'net' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Type, "Order") != null, "Type 'Order' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Schema, "OrderSchema") != null, "Schema 'OrderSchema' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Tool, "validate_patch") != null, "Tool 'validate_patch' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Function, "process") != null, "Function 'process' should be in symbol table.");
    AssertTrue(table.LookupDocument(SymbolKind.Actor, "BuildWorker") != null, "Actor 'BuildWorker' should be in symbol table.");

    var actorMembers = table.GetActorMembers("BuildWorker");
    AssertTrue(actorMembers.Count == 2, $"Actor 'BuildWorker' should have 2 members (state + handler), got {actorMembers.Count}.");
    AssertTrue(actorMembers.Any(s => s.Kind == SymbolKind.ActorState && s.Name == "current"), "Actor state 'current' should be in actor scope.");
    AssertTrue(actorMembers.Any(s => s.Kind == SymbolKind.ActorHandler && s.Name == "handle"), "Actor handler 'handle' should be in actor scope.");

    AssertTrue(!semanticResult.SemanticDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Valid document should produce no semantic error diagnostics.");
    AssertTrue(semanticResult.Success, "Semantic result should report success for a valid document.");
}

static void SemanticModelDetectsDuplicateDeclarationsInDocumentScope()
{
    const string source = """
        intent "Test duplicate semantic diagnostics"
        capability OrdersApi
        fn process(api: OrdersApi) -> Result<string, Error>
            requires api != empty
        {
            return Ok("")
        }
        fn process(api: OrdersApi) -> Result<string, Error>
            requires api != empty
        {
            return Ok("")
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var ai3001 = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3001" && d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    AssertTrue(ai3001.Length >= 1, $"Expected at least one AI3001 diagnostic for duplicate function, got {ai3001.Length}.");
    AssertTrue(ai3001.All(d => d.Message.Contains("process")), "AI3001 message should reference the duplicate name 'process'.");
}

static void SemanticModelDetectsCrossKindDuplicateDeclarationsInDocumentScope()
{
    const string source = """
        intent "Test cross-kind semantic duplicates"
        capability SharedName
        resource SharedName
        fn process(api: SharedName) -> Result<string, Error>
            requires api != empty
        {
            return Ok("")
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var ai3001 = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3001" && d.Severity == DiagnosticSeverity.Error && d.Message.Contains("SharedName"))
        .ToArray();

    AssertTrue(ai3001.Length >= 1, $"Expected AI3001 for cross-kind duplicate document symbol 'SharedName', got {ai3001.Length}.");
}

static void SemanticModelPreservesModuleAndEffectSourceLocations()
{
    const string source = """
        module sales.orders
        intent "Symbol locations"
        effect net, json
        fn process() -> string
            requires true
        {
            return ""
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var module = semanticResult.SymbolTable.LookupDocument(SymbolKind.Module, "sales.orders")
        ?? throw new InvalidOperationException("Module symbol should be present.");
    var net = semanticResult.SymbolTable.LookupDocument(SymbolKind.Effect, "net")
        ?? throw new InvalidOperationException("Effect symbol 'net' should be present.");
    var json = semanticResult.SymbolTable.LookupDocument(SymbolKind.Effect, "json")
        ?? throw new InvalidOperationException("Effect symbol 'json' should be present.");

    AssertTrue(module.Location.Line == 1 && module.Location.Column == 8, $"Module symbol should point at module name, got {module.Location}.");
    AssertTrue(net.Location.Line == 3 && net.Location.Column == 8, $"Effect 'net' should point at effect name, got {net.Location}.");
    AssertTrue(json.Location.Line == 3 && json.Location.Column == 13, $"Effect 'json' should point at effect name, got {json.Location}.");
}

static void SemanticModelDetectsDuplicateEffectDeclarations()
{
    const string source = """
        intent "Duplicate effect"
        effect net, net
        fn process() -> string
            effects [net]
            requires true
        {
            return ""
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var duplicates = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3001" && d.Message.Contains("net", StringComparison.Ordinal))
        .ToArray();

    AssertTrue(duplicates.Length >= 1, "Expected semantic duplicate diagnostic AI3001 for duplicate effect 'net'.");
    AssertTrue(duplicates.Any(d => d.Location.Line == 2 && d.Location.Column == 13), "Duplicate effect diagnostic should use the second effect location.");
}

static void SemanticModelResolvesActorMembersInActorScope()
{
    const string source = """
        intent "Test actor scope lookup"
        actor OrderActor {
            state pending: string
            state total: string
            async fn submit(order: string) -> Result<string, Error>
                requires order != empty
            {
                return Ok(order)
            }
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    AssertTrue(semanticResult.SymbolTable.LookupActorMember("OrderActor", "pending")?.Kind == SymbolKind.ActorState,
        "Actor state 'pending' should be resolvable in actor scope.");
    AssertTrue(semanticResult.SymbolTable.LookupActorMember("OrderActor", "total")?.Kind == SymbolKind.ActorState,
        "Actor state 'total' should be resolvable in actor scope.");
    AssertTrue(semanticResult.SymbolTable.LookupActorMember("OrderActor", "submit")?.Kind == SymbolKind.ActorHandler,
        "Actor handler 'submit' should be resolvable in actor scope.");
    AssertTrue(semanticResult.SymbolTable.LookupActorMember("OrderActor", "missing") == null,
        "Unknown actor member should return null.");
    AssertTrue(semanticResult.SymbolTable.LookupActorMember("UnknownActor", "pending") == null,
        "Member lookup for unknown actor should return null.");
    AssertTrue(!semanticResult.SemanticDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
        "No semantic error diagnostics expected for valid actor.");
}

static void SemanticModelDetectsDuplicateActorMembers()
{
    const string source = """
        intent "Test duplicate actor member detection"
        actor Processor {
            state queue: string
            state queue: string
            async fn run(input: string) -> Result<string, Error>
                requires input != empty
            {
                return Ok(input)
            }
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var ai3002 = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3002" && d.Severity == DiagnosticSeverity.Error)
        .ToArray();

    AssertTrue(ai3002.Length >= 1, $"Expected at least one AI3002 diagnostic for duplicate actor member 'queue', got {ai3002.Length}.");
    AssertTrue(ai3002.All(d => d.Message.Contains("Processor")), "AI3002 message should reference the actor name 'Processor'.");
    AssertTrue(ai3002.All(d => d.Message.Contains("queue")), "AI3002 message should reference the duplicate member name 'queue'.");
}

static void NameResolutionResolvesDeclaredTypesAndPrimitives()
{
    // No use imports — any unknown name should produce AI3010.
    // All types here ARE declared or are primitives, so zero errors expected.
    const string source = """
        intent "Name resolution test"
        capability OrdersApi
        type OrderStatus = Active | Archived
        schema OrderSchema = { id: string }
        fn process(api: OrdersApi) -> Result<string, Error>
            requires api != empty
        {
            return Ok("")
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length == 0, $"Expected no AI3010 errors for fully-declared types, got: {string.Join(", ", ai3010.Select(d => d.Message))}");
    AssertTrue(result.Success, "Semantic result should be successful for a document with fully-resolved types.");
}

static void NameResolutionDetectsUndeclaredTypeNames()
{
    // No use imports, so unknown type names should produce AI3010.
    const string source = """
        intent "Name resolution test"
        fn process(x: UnknownFoo) -> string
            requires x != empty
        {
            return ""
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length >= 1, $"Expected at least one AI3010 for undeclared type 'UnknownFoo', got {ai3010.Length}.");
    AssertTrue(ai3010.Any(d => d.Message.Contains("UnknownFoo")), "AI3010 message should reference the unresolved type name.");
}

static void NameResolutionAllowsUnknownNamesWhenUseImportsPresent()
{
    // With use imports, unknown type names should NOT produce AI3010.
    const string source = """
        use std.collections
        intent "Name resolution test"
        fn process(x: SomeImportedType) -> SomeOtherImportedType
            requires x != empty
        {
            return Ok(x)
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length == 0, $"Expected no AI3010 errors when use imports are present, got: {string.Join(", ", ai3010.Select(d => d.Message))}");
}

static void NameResolutionResolvesSchemaFieldTypes()
{
    // Schema fields must reference declared types or primitives.
    const string source = """
        intent "Schema field resolution"
        type OrderStatus = Active | Archived
        schema OrderSchema = { id: string, status: OrderStatus }
        fn stub() -> string { return "" }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length == 0, $"Expected no AI3010 errors for schema with declared field types, got: {string.Join(", ", ai3010.Select(d => d.Message))}");

    // Now add a schema field with an undeclared type.
    const string sourceBad = """
        intent "Schema field resolution"
        schema OrderSchema = { id: string, status: GhostType }
        fn stub() -> string { return "" }
        """;

    var resultBad = AISharpSemanticModel.Build(AISharpParser.Parse(sourceBad));
    var ai3010bad = resultBad.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010bad.Length >= 1, $"Expected AI3010 for undeclared schema field type 'GhostType', got {ai3010bad.Length}.");
    AssertTrue(ai3010bad.Any(d => d.Message.Contains("GhostType")), "AI3010 message should name 'GhostType'.");
}

static void NameResolutionResolvesActorStateTypes()
{
    // Actor state types must reference declared types or primitives.
    const string source = """
        intent "Actor state resolution"
        type JobStatus = Pending | Running | Done
        actor Worker {
            state status: JobStatus
            state count: int
            async fn run(job: string) -> Result<string, Error>
                requires job != empty
            {
                return Ok(job)
            }
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length == 0, $"Expected no AI3010 for actor state with declared type 'JobStatus', got: {string.Join(", ", ai3010.Select(d => d.Message))}");

    // Undeclared actor state type.
    const string sourceBad = """
        intent "Actor state resolution"
        actor Worker {
            state status: Phantom
            async fn run(job: string) -> Result<string, Error>
                requires job != empty
            {
                return Ok(job)
            }
        }
        """;

    var resultBad = AISharpSemanticModel.Build(AISharpParser.Parse(sourceBad));
    var ai3010bad = resultBad.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010bad.Length >= 1, $"Expected AI3010 for undeclared actor state type 'Phantom', got {ai3010bad.Length}.");
}

static void NameResolutionResolvesGenericArgumentTypes()
{
    const string source = """
        intent "Generic argument resolution"
        schema Order = { id: string }
        fn process() -> Result<List<Order>, Error>
            requires true
        {
            return Ok([])
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var ai3010 = result.SemanticDiagnostics.Where(d => d.Code == "AI3010").ToArray();
    AssertTrue(ai3010.Length == 0, $"Expected no AI3010 for declared generic argument 'Order', got: {string.Join(", ", ai3010.Select(d => d.Message))}");
    AssertTrue(result.ResolvedTypeReferences.Any(r =>
        r.TypeText == "Order"
        && r.Context == "return:process"
        && r.ResolvedSymbol?.Kind == SymbolKind.Schema), "Expected generic argument 'Order' to resolve to the schema declaration.");

    const string sourceBad = """
        intent "Generic argument diagnostics"
        fn process() -> Result<List<GhostOrder>, Error>
            requires true
        {
            return Ok([])
        }
        """;

    var resultBad = AISharpSemanticModel.Build(AISharpParser.Parse(sourceBad));
    var diagnostic = resultBad.SemanticDiagnostics.SingleOrDefault(d => d.Code == "AI3010" && d.Message.Contains("GhostOrder", StringComparison.Ordinal));
    AssertTrue(diagnostic is not null, "Expected AI3010 for undeclared nested generic argument 'GhostOrder'.");
    AssertTrue(diagnostic!.Location.Line == 2, "Expected nested generic diagnostic to retain function signature source line.");
}

static void NameResolutionCollectsResolvedSymbolReferences()
{
    const string source = """
        intent "Resolved symbol references"
        capability Network
        resource Socket
        effect network
        schema Reply = { message: string }
        tool fetch(url: string) -> Reply {
            sandbox = wasm
            capabilities = [Network]
            timeout = 30s
        }
        fn run(socket: Socket) -> Reply
            effects [network]
            requires socket != empty
        {
            return model local "qwen3-coder"
                .task("reply")
                .input(socket)
                .output<Reply>()
                .run()
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    AssertTrue(result.Success, "Expected all PR-02 symbol references to resolve without diagnostics.");
    var refs = result.ResolvedSymbolReferences;
    AssertTrue(refs.Any(r => r.Name == "Socket" && r.ReferenceKind == SymbolReferenceKind.Resource && r.ResolvedSymbol?.Kind == SymbolKind.Resource), "Expected resource parameter reference metadata.");
    AssertTrue(refs.Any(r => r.Name == "Network" && r.ReferenceKind == SymbolReferenceKind.ToolCapability && r.ResolvedSymbol?.Kind == SymbolKind.Capability), "Expected tool capability reference metadata.");
    AssertTrue(refs.Any(r => r.Name == "network" && r.ReferenceKind == SymbolReferenceKind.Effect && r.ResolvedSymbol?.Kind == SymbolKind.Effect), "Expected effect reference metadata.");
    AssertTrue(refs.Any(r => r.Name == "Reply" && r.ReferenceKind == SymbolReferenceKind.ModelOutputSchema && r.ResolvedSymbol?.Kind == SymbolKind.Schema), "Expected model output schema reference metadata.");
}

static void NameResolutionCollectsResolvedTypeReferences()
{
    const string source = """
        intent "Type reference collection"
        capability OrdersApi
        type OrderStatus = Active | Archived
        fn process(api: OrdersApi) -> Result<OrderStatus, Error>
            requires api != empty
        {
            return Ok(Active)
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var refs = result.ResolvedTypeReferences;

    AssertTrue(refs.Count > 0, "Expected at least one resolved type reference.");
    var apiRef = refs.FirstOrDefault(r => r.TypeText == "OrdersApi");
    AssertTrue(apiRef != null, "Expected a TypeReference for 'OrdersApi' parameter.");
    AssertTrue(apiRef!.ResolvedSymbol?.Kind == SymbolKind.Capability, "OrdersApi reference should resolve to a Capability symbol.");
    var returnRef = refs.FirstOrDefault(r => r.TypeText.StartsWith("Result<", StringComparison.Ordinal));
    AssertTrue(returnRef != null, "Expected a TypeReference for the Result<...> return type.");
}

static void TypeCheckerPassesGetActiveOrders()
{
    // The sample getActiveOrders function should type-check without AI3020 diagnostics.
    var source = """
        intent "Type checker sample"
        capability OrdersApi
        type OrderStatus = Active | Archived
        type OrderError = NetworkFailure | Unauthorized
        type Order = {
            id: OrderId,
            status: OrderStatus
        }
        fn getActiveOrders(customerId: CustomerId, api: OrdersApi)
            -> Result<List<Order>, OrderError>
            effects [network]
            requires customerId != empty
        {
            let response = await api.get("/customers/{customerId}/orders")
            let orders = decode<List<Order>>(response)?
            return Ok(orders.filter(o => o.status == Active))
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var typeDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3020")
        .ToList();
    AssertTrue(typeDiags.Count == 0,
        $"Expected no AI3020 type mismatches for getActiveOrders, got: {string.Join("; ", typeDiags.Select(d => d.Message))}");
}

static void TypeCheckerDetectsReturnTypeMismatch()
{
    var source = """
        intent "Return type mismatch"
        fn badReturn() -> int
        {
            return "this is a string"
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var typeDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3020")
        .ToList();
    AssertTrue(typeDiags.Count > 0,
        "Expected AI3020 return type mismatch diagnostic when returning string from int function.");
}

static void TypeCheckerDetectsLetAnnotationMismatch()
{
    var source = """
        intent "Let annotation mismatch"
        fn badLet() -> unit
        {
            let x: int = "hello"
            return unit
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var typeDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3021")
        .ToList();
    AssertTrue(typeDiags.Count > 0,
        "Expected AI3021 annotation mismatch diagnostic when assigning string to int let binding.");
}

static void TypeCheckerTypesModelCallOutputAsSchema()
{
    // InferExpressionType should return the schema name for a .output<Schema>() call.
    var outputExpr = "session.prompt(\"summarize\").output<OrderPatch>()";
    var inferred = AISharpTypeChecker.InferExpressionType(outputExpr);
    AssertTrue(inferred is NamedAIType { Name: "OrderPatch" },
        $"Expected NamedAIType(OrderPatch) from model output expression, got: {inferred}");
}

static void ResourceLifetimeAllowsOwnedTransferOnce()
{
    const string source = """
        intent "Owned transfer"
        resource File
        fn close(file: owned File) -> Result<unit, Error>
            requires true
        {
            using file {
                return Ok(unit)
            }
        }
        fn bridge(file: owned File) -> Result<unit, Error>
            requires true
        {
            return close(file)
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var ownershipDiagnostics = result.SemanticDiagnostics
        .Where(d => d.Code.StartsWith("AI307", StringComparison.Ordinal))
        .ToArray();
    AssertTrue(ownershipDiagnostics.Length == 0,
        $"Expected a single owned transfer to validate, got: {string.Join("; ", ownershipDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void ResourceLifetimeDetectsUseAfterTransfer()
{
    const string source = """
        intent "Use after transfer"
        resource File
        fn close(file: owned File) -> Result<unit, Error>
            requires true
        {
            using file {
                return Ok(unit)
            }
        }
        fn duplicate(file: owned File) -> Result<unit, Error>
            requires true
        {
            let first = close(file)
            return close(file)
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    AssertTrue(result.SemanticDiagnostics.Any(d => d.Code == "AI3070"),
        $"Expected AI3070 use-after-transfer diagnostic, got: {string.Join("; ", result.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void ResourceLifetimeRejectsBorrowedResourceEscape()
{
    const string source = """
        intent "Borrowed escape"
        resource Secret<T>
        actor SecretCache {
            state cached: Option<Secret<ApiKey>>
            async fn remember(key: borrowed Secret<ApiKey>) -> Result<unit, Error>
                requires true
            {
                self.cached = key
                return Ok(unit)
            }
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    AssertTrue(result.SemanticDiagnostics.Any(d => d.Code == "AI3071"),
        $"Expected AI3071 borrowed escape diagnostic, got: {string.Join("; ", result.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void ResourceLifetimeRequiresOwnedResourceDisposalOrTransfer()
{
    const string source = """
        intent "Missing disposal"
        resource File
        fn leak(file: owned File) -> Result<unit, Error>
            requires true
        {
            return Ok(unit)
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    AssertTrue(result.SemanticDiagnostics.Any(d => d.Code == "AI3072"),
        $"Expected AI3072 deterministic disposal diagnostic, got: {string.Join("; ", result.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void ResourceLifetimeAcceptsUsingDisposal()
{
    const string source = """
        intent "Using disposal"
        resource File
        fn close(file: owned File) -> Result<unit, Error>
            requires true
        {
            using file {
                return Ok(unit)
            }
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var ownershipDiagnostics = result.SemanticDiagnostics
        .Where(d => d.Code.StartsWith("AI307", StringComparison.Ordinal))
        .ToArray();
    AssertTrue(ownershipDiagnostics.Length == 0,
        $"Expected lexical using to dispose the owned resource, got: {string.Join("; ", ownershipDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void ResourceLifetimeKeepsOrdinaryValuesErgonomic()
{
    const string source = """
        intent "GC values"
        fn combine(name: string) -> string
            requires true
        {
            let first = name
            return name
        }
        """;

    var result = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var ownershipDiagnostics = result.SemanticDiagnostics
        .Where(d => d.Code.StartsWith("AI307", StringComparison.Ordinal))
        .ToArray();
    AssertTrue(ownershipDiagnostics.Length == 0,
        $"Expected ordinary GC-managed values to remain unrestricted, got: {string.Join("; ", ownershipDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void TypeCheckerModelsGenericCarriersAndFunctionTypes()
{
    var table = new SymbolTable();

    var result = AISharpTypeChecker.ParseType("Result<List<Order>, OrderError>", table);
    AssertTrue(result is ResultAIType, $"Expected ResultAIType, got {result}.");
    var resultType = (ResultAIType)result;
    AssertTrue(resultType.OkType is GenericAIType { Head: "List", Args.Count: 1 },
        $"Expected Result ok type to be List<Order>, got {resultType.OkType}.");
    AssertTrue(resultType.ErrorType is NamedAIType { Name: "OrderError" },
        $"Expected Result error type to be OrderError, got {resultType.ErrorType}.");

    var option = AISharpTypeChecker.ParseType("Option<Order>", table);
    AssertTrue(option is OptionAIType { ValueType: NamedAIType { Name: "Order" } },
        $"Expected OptionAIType<Order>, got {option}.");

    var fn = AISharpTypeChecker.ParseType("fn(int, string) -> bool", table);
    AssertTrue(fn is FunctionAIType, $"Expected FunctionAIType, got {fn}.");
    var functionType = (FunctionAIType)fn;
    AssertTrue(functionType.Parameters.Count == 2, "Expected two function parameters.");
    AssertTrue(functionType.ReturnType is PrimitiveAIType { Name: "bool" },
        $"Expected bool function return, got {functionType.ReturnType}.");
}

static void TypeCheckerTypesSimpleFunctionCalls()
{
    const string source = """
        intent "Simple call typing"
        fn count() -> int
        {
            return 1
        }
        fn label() -> string
        {
            return count()
        }
        fn badLet() -> unit
        {
            let text: string = count()
            return unit
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var returnMismatch = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3020")
        .ToList();
    AssertTrue(returnMismatch.Count >= 1,
        $"Expected AI3020 for returning count() from string function, got: {string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");

    var letMismatch = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3021")
        .ToList();
    AssertTrue(letMismatch.Count >= 1,
        $"Expected AI3021 for let text: string = count(), got: {string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
}

static void TypeCheckerValidatesRecordFieldAccess()
{
    const string source = """
        intent "Record field access"
        type OrderStatus = Active | Archived
        type Order = {
            id: OrderId,
            status: OrderStatus
        }
        fn statusOf(order: Order) -> OrderStatus
        {
            return order.status
        }
        fn badField(order: Order) -> OrderId
        {
            return order.customerId
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));

    var returnMismatch = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3020")
        .ToList();
    AssertTrue(returnMismatch.Count == 0,
        $"Expected order.status to type as OrderStatus without AI3020, got: {string.Join("; ", returnMismatch.Select(d => d.Message))}");

    var missingField = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3023")
        .ToList();
    AssertTrue(missingField.Count >= 1,
        $"Expected AI3023 for missing record field customerId, got: {string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    AssertTrue(missingField.Any(d => d.Message.Contains("customerId", StringComparison.Ordinal)),
        "Missing field diagnostic should mention customerId.");
}

static void HirBuilderBuildsDocumentForSample()
{
    // The HIR builder must build a document for samples/orders.aisharp without throwing.
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var source = File.ReadAllText(samplePath);
    var parseResult   = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult     = AISharpHirBuilder.Build(semanticResult);

    var hirDoc = hirResult.Document ?? throw new InvalidOperationException("HIR document should not be null.");
    AssertTrue(hirDoc.Functions.Count > 0, "HIR document should have at least one function.");
    AssertTrue(hirDoc.Types.Count > 0, "HIR document should have at least one type declaration.");
    AssertTrue(hirResult.SourceMap.Count > 0, "Source map should contain entries.");
}

static void HirBuilderAssignsStableIdsToDeclations()
{
    const string source = """
        intent "HIR stable IDs"
        type OrderStatus = Active | Archived
        schema OrderSchema { id: string }
        tool validate(x: string) -> bool
            sandbox = wasm
        fn process(id: string) -> string
        {
            return id
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var doc            = hirResult.Document;

    AssertTrue(doc.Id.Value == "doc:(anonymous)", $"Document ID should be 'doc:(anonymous)', got '{doc.Id}'.");

    var typeDecl = doc.Types.Single(t => t.Name == "OrderStatus");
    AssertTrue(typeDecl.Id.Value == "type:OrderStatus", $"Type ID mismatch: {typeDecl.Id}");

    var schema = doc.Schemas.Single(s => s.Name == "OrderSchema");
    AssertTrue(schema.Id.Value == "schema:OrderSchema", $"Schema ID mismatch: {schema.Id}");

    var tool = doc.Tools.Single(t => t.Name == "validate");
    AssertTrue(tool.Id.Value == "tool:validate", $"Tool ID mismatch: {tool.Id}");

    var fn = doc.Functions.Single(f => f.Name == "process");
    AssertTrue(fn.Id.Value == "fn:process", $"Function ID mismatch: {fn.Id}");
}

static void HirBuilderRetainsSourceLocations()
{
    const string source = """
        intent "HIR source map"
        fn echo(x: string) -> string
        {
            return x
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);

    var fn = hirResult.Document.Functions.Single();
    AssertTrue(fn.Location.Line > 0, "Function HIR node should have a valid source location line.");

    // Source map should contain the function's stable ID.
    AssertTrue(hirResult.SourceMap.ContainsKey("fn:echo"), "Source map should contain 'fn:echo'.");
    var mappedLoc = hirResult.SourceMap["fn:echo"];
    AssertTrue(mappedLoc.Line == fn.Location.Line, "Source map location should match HIR node location.");
}

static void HirBuilderExposesResolvedSemanticReferences()
{
    const string source = """
        intent "HIR semantic references"
        schema Reply = { message: string }
        fn complete(prompt: string) -> Reply
            effects [model]
        {
            return model local "qwen3-coder"
                .task("reply")
                .input(prompt)
                .output<Reply>()
                .run()
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);

    AssertTrue(hirResult.ResolvedTypeReferences.Any(r =>
        r.TypeText == "Reply" &&
        r.Context == "return:complete" &&
        r.ResolvedSymbol?.Kind == SymbolKind.Schema),
        "HIR build result should expose resolved return type references.");

    AssertTrue(hirResult.ResolvedSymbolReferences.Any(r =>
        r.Name == "Reply" &&
        r.ReferenceKind == SymbolReferenceKind.ModelOutputSchema &&
        r.Context == "model-output:complete" &&
        r.ResolvedSymbol?.Kind == SymbolKind.Schema),
        "HIR build result should expose resolved model output schema references.");
}

static void HirBuilderPreservesModelCallMetadata()
{
    const string source = """
        intent "HIR model metadata"
        schema PlanDraft = { summary: string }
        fn choose(prompt: string) -> PlanDraft
            effects [model]
        {
            return model local "qwen3-coder"
                .task("Produce a deployment plan")
                .input(prompt)
                .output<PlanDraft>()
                .replay(seed: 7, temperature: 0.0)
                .capture_provenance()
                .run()
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);

    var modelCall = hirResult.Document.Functions.Single().ModelCalls.Single();
    AssertTrue(modelCall.OutputSchema == "PlanDraft", "HIR model call should preserve output schema name.");
    AssertTrue(modelCall.OutputType is RecordAIType { Name: "PlanDraft" },
        $"HIR model output should resolve to the schema type, got {modelCall.OutputType}.");
    AssertTrue(modelCall.Replay?.Seed == 7, "HIR model call should preserve replay seed.");
    AssertTrue(modelCall.Replay?.Temperature == 0.0m, "HIR model call should preserve replay temperature.");
    AssertTrue(modelCall.CapturesProvenance, "HIR model call should preserve provenance capture metadata.");
}

static void HirBuilderLowersFunctionBodiesToHirStatements()
{
    const string source = """
        intent "HIR statements"
        fn compute(x: int) -> int
        {
            let y: int = 42
            return y
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);

    var fn = hirResult.Document.Functions.Single();
    AssertTrue(fn.Body.Count == 2, $"Expected 2 HIR body statements, got {fn.Body.Count}.");

    var letStmt = fn.Body[0] as HirLetStatement;
    AssertTrue(letStmt is not null, "First body statement should be HirLetStatement.");
    AssertTrue(letStmt!.Name == "y", $"Let statement name should be 'y', got '{letStmt.Name}'.");
    AssertTrue(letStmt.AnnotationType is PrimitiveAIType { Name: "int" },
        $"Let annotation type should be PrimitiveAIType(int), got {letStmt.AnnotationType}.");

    var retStmt = fn.Body[1] as HirReturnStatement;
    AssertTrue(retStmt is not null, "Second body statement should be HirReturnStatement.");
    AssertTrue(retStmt!.ExpressionText == "y", $"Return expression should be 'y', got '{retStmt.ExpressionText}'.");
}

// ── PR-05 Effect Checker tests ────────────────────────────────────────────────

static void EffectCheckerPassesSampleWithNetworkEffects()
{
    // orders.aisharp declares effects [network] for getActiveOrders which calls api.get().
    // The effect checker must not produce AI3030/AI3031 diagnostics for that file.
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var source = File.ReadAllText(samplePath);
    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code is "AI3030" or "AI3031")
        .ToList();

    AssertTrue(effectDiags.Count == 0,
        $"Expected no effect diagnostics for sample, got: {string.Join(", ", effectDiags.Select(d => d.Message))}");

    var effectResult = semanticResult.EffectCheckResult;
    AssertTrue(effectResult is not null, "EffectCheckResult must not be null after Build().");
    AssertTrue(effectResult!.FunctionEffects.Count > 0, "FunctionEffects must list at least one function.");
}

static void EffectCheckerDetectsMissingModelEffect()
{
    // A function with a model call but no 'model' effect declaration must get AI3030.
    const string source = """
        intent "effect test"
        schema Completion = { text: string }
        fn callAi(prompt: string) -> string
        {
            return model openai "gpt-4"
                .task("summarize")
                .input(prompt)
                .output<Completion>()
                .run()
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiag = semanticResult.SemanticDiagnostics
        .FirstOrDefault(d => d.Code == "AI3030");

    AssertTrue(effectDiag is not null,
        "Expected AI3030 diagnostic for missing 'model' effect, but none was produced.");
    AssertTrue(effectDiag!.Message.Contains("model"),
        $"AI3030 message should mention 'model', got: {effectDiag.Message}");
}

static void EffectCheckerDetectsMissingNetworkEffect()
{
    // A function calling http.get without declaring effects [network] must get AI3030.
    const string source = """
        intent "network effect test"
        fn fetchData(url: string) -> string
        {
            let resp = http.get(url)
            return resp
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiag = semanticResult.SemanticDiagnostics
        .FirstOrDefault(d => d.Code == "AI3030");

    AssertTrue(effectDiag is not null,
        "Expected AI3030 diagnostic for missing 'network' effect, but none was produced.");
    AssertTrue(effectDiag!.Message.Contains("network"),
        $"AI3030 message should mention 'network', got: {effectDiag.Message}");
}

static void EffectCheckerDetectsPureFunctionCallingEffectfulFunction()
{
    // Effects required by a callee must be declared by the caller too.
    const string source = """
        intent "call effect propagation"
        fn fetchOrder(url: string) -> string
            effects [network]
        {
            return http.get(url)
        }

        fn summarize(url: string) -> string
        {
            return fetchOrder(url)
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiag = semanticResult.SemanticDiagnostics
        .FirstOrDefault(d => d.Code == "AI3030" && d.Message.Contains("summarize", StringComparison.Ordinal));

    AssertTrue(effectDiag is not null,
        "Expected AI3030 diagnostic when pure function 'summarize' calls network-effect function 'fetchOrder'.");
    AssertTrue(effectDiag!.Message.Contains("network", StringComparison.Ordinal),
        $"AI3030 message should mention propagated 'network' effect, got: {effectDiag.Message}");
}

static void EffectCheckerDetectsMissingActorHandlerEffect()
{
    const string source = """
        intent "actor handler effect check"
        actor Fetcher {
            async fn handle(url: string) -> string
            {
                return http.get(url)
            }
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiag = semanticResult.SemanticDiagnostics
        .FirstOrDefault(d => d.Code == "AI3031");

    AssertTrue(effectDiag is not null,
        "Expected AI3031 diagnostic when actor handler uses network without declaring effects [network].");
    AssertTrue(effectDiag!.Message.Contains("network", StringComparison.Ordinal),
        $"AI3031 message should mention 'network', got: {effectDiag.Message}");
}

static void EffectCheckerReportsCompliantPureFunction()
{
    // A pure function with no effects declared and no effectful expressions must be compliant.
    const string source = """
        intent "pure function"
        fn add(a: int, b: int) -> int
        {
            return a
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var effectDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code is "AI3030" or "AI3031")
        .ToList();

    AssertTrue(effectDiags.Count == 0,
        $"Pure function should have no effect diagnostics, got: {string.Join(", ", effectDiags.Select(d => d.Message))}");

    var info = semanticResult.EffectCheckResult?.FunctionEffects
        .FirstOrDefault(f => f.FunctionName == "add");

    AssertTrue(info is not null, "FunctionEffects must include 'add'.");
    AssertTrue(info!.IsCompliant, "Pure 'add' function should be marked compliant.");
    AssertTrue(info.InferredRequiredEffects.Count == 0,
        $"No inferred effects expected, got: {string.Join(", ", info.InferredRequiredEffects)}");
}

// ── Match statement parsing tests ─────────────────────────────────────────────

static void MatchStatementParsesBasicMatchExpression()
{
    // A match expression with named arms must be parsed into MatchStatement nodes in the body.
    const string source = """
        intent "match test"
        type Status = Active | Archived
        fn describe(status: Status) -> string
            requires true
        {
            match status {
                Active => "active"
                Archived => "archived"
            }
            return status
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    AssertTrue(parseResult.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error),
        $"Expected no parse errors, got: {string.Join("; ", parseResult.Diagnostics.Select(d => d.Message))}");

    var fn = parseResult.Document.Functions.Single();
    var matchStmt = fn.Body.Statements.OfType<MatchStatement>().FirstOrDefault();
    AssertTrue(matchStmt is not null, "Expected a MatchStatement in the function body.");
    AssertTrue(matchStmt!.Scrutinee == "status",
        $"Scrutinee should be 'status', got '{matchStmt.Scrutinee}'.");
    AssertTrue(matchStmt.Arms.Count == 2,
        $"Expected 2 match arms, got {matchStmt.Arms.Count}.");
    AssertTrue(matchStmt.Arms[0].Pattern == "Active",
        $"First arm pattern should be 'Active', got '{matchStmt.Arms[0].Pattern}'.");
    AssertTrue(matchStmt.Arms[1].Pattern == "Archived",
        $"Second arm pattern should be 'Archived', got '{matchStmt.Arms[1].Pattern}'.");
}

static void MatchStatementReportsNonExhaustiveMatch()
{
    const string source = """
        intent "non-exhaustive match"
        type Status = Active | Archived | Pending
        fn label(status: Status) -> string
            requires true
        {
            match status {
                Active => "active"
                Archived => "archived"
            }
            return status
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);

    var matchStmt = parseResult.Document.Functions.Single()
        .Body.Statements.OfType<MatchStatement>().FirstOrDefault();
    AssertTrue(matchStmt is not null, "MatchStatement must be present in the body.");
    AssertTrue(matchStmt!.Arms.Count == 2,
        $"Expected 2 arms (only Active and Archived covered), got {matchStmt.Arms.Count}.");

    var exhaustDiags = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3060")
        .ToList();
    AssertTrue(exhaustDiags.Count >= 1,
        $"Expected AI3060 exhaustiveness error for missing 'Pending', got {exhaustDiags.Count}: " +
        $"{string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    AssertTrue(exhaustDiags[0].Message.Contains("Pending"),
        $"Error should mention missing case 'Pending', got: {exhaustDiags[0].Message}");
}

static void MatchStatementAcceptsWildcardArm()
{
    // A match with a wildcard '_' arm must parse and the wildcard arm must be preserved.
    const string source = """
        intent "wildcard arm"
        type Status = Active | Archived | Pending
        fn label(status: Status) -> string
            requires true
        {
            match status {
                Active => "active"
                _ => "other"
            }
            return status
        }
        """;

    var parseResult = AISharpParser.Parse(source);
    AssertTrue(parseResult.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error),
        $"Expected no parse errors for wildcard match, got: {string.Join("; ", parseResult.Diagnostics.Select(d => d.Message))}");

    var fn = parseResult.Document.Functions.Single();
    var matchStmt = fn.Body.Statements.OfType<MatchStatement>().FirstOrDefault();
    AssertTrue(matchStmt is not null, "Expected a MatchStatement in the function body.");
    AssertTrue(matchStmt!.Arms.Count == 2,
        $"Expected 2 arms, got {matchStmt.Arms.Count}.");

    var wildcard = matchStmt.Arms.FirstOrDefault(a => a.Pattern == "_");
    AssertTrue(wildcard is not null, "Wildcard arm '_' should be present in match arms.");
    AssertTrue(wildcard!.Body == "\"other\"",
        $"Wildcard arm body should be '\"other\"', got '{wildcard.Body}'.");
}

static void MatchStatementReportsDuplicateAndUnreachableArms()
{
    const string source = """
        intent "duplicate match arms"
        type Status = Active | Archived | Pending
        fn label(status: Status) -> string
            requires true
        {
            match status {
                Active => "active"
                Active => "again"
                _ => "other"
                Pending => "pending"
            }
            return "done"
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var armDiagnostics = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3061")
        .ToArray();

    AssertTrue(armDiagnostics.Length >= 2,
        $"Expected AI3061 for duplicate Active and unreachable Pending after wildcard, got: {string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    AssertTrue(armDiagnostics.Any(d => d.Message.Contains("Active", StringComparison.Ordinal)),
        "Duplicate arm diagnostic should mention Active.");
    AssertTrue(armDiagnostics.Any(d => d.Message.Contains("Pending", StringComparison.Ordinal)),
        "Unreachable arm diagnostic should mention Pending.");
}

static void MatchStatementTypeChecksArmResultTypes()
{
    const string source = """
        intent "match arm typing"
        type Status = Active | Archived
        fn label(status: Status) -> string
            requires true
        {
            match status {
                Active => "active"
                Archived => 42
            }
            return "done"
        }
        """;

    var semanticResult = AISharpSemanticModel.Build(AISharpParser.Parse(source));
    var armTypeDiagnostics = semanticResult.SemanticDiagnostics
        .Where(d => d.Code == "AI3062")
        .ToArray();

    AssertTrue(armTypeDiagnostics.Length >= 1,
        $"Expected AI3062 for incompatible match arm result types, got: {string.Join("; ", semanticResult.SemanticDiagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    AssertTrue(armTypeDiagnostics[0].Message.Contains("Archived", StringComparison.Ordinal),
        $"Arm result type diagnostic should mention Archived, got: {armTypeDiagnostics[0].Message}");
}

static void CliJsonEmitsMatchStatementStructure()
{
    var sourcePath = Path.Combine(Path.GetTempPath(), $"aisharp-match-{Guid.NewGuid():N}.aisharp");
    File.WriteAllText(sourcePath, """
        intent "match json"
        type OrderStatus = Active | Archived
        fn describe(status: OrderStatus) -> string
            requires true
        {
            match status {
                Active => "active"
                Archived => "archived"
            }
            return "done"
        }
        """);

    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{CliCommandPrefix()} {Quote(sourcePath)} --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start AISharp.Cli process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0, $"Expected CLI JSON command to succeed. Exit {process.ExitCode}. STDERR: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var matchStatement = root.GetProperty("functions")[0]
            .GetProperty("body")
            .GetProperty("statements")
            .EnumerateArray()
            .Single(statement => statement.GetProperty("kind").GetString() == "match");

        AssertTrue(matchStatement.GetProperty("scrutinee").GetString() == "status", "Match scrutinee was not emitted.");
        AssertTrue(matchStatement.GetProperty("location").GetProperty("line").GetInt32() > 0, "Match location line was not emitted.");

        var arms = matchStatement.GetProperty("arms").EnumerateArray().ToArray();
        AssertTrue(arms.Length == 2, $"Expected 2 match arms in CLI JSON, got {arms.Length}.");
        AssertTrue(arms[0].GetProperty("pattern").GetString() == "Active", "First match arm pattern was not emitted.");
        AssertTrue(arms[0].GetProperty("body").GetString() == "\"active\"", "First match arm body was not emitted.");
        AssertTrue(arms[0].GetProperty("location").GetProperty("line").GetInt32() > 0, "Match arm location line was not emitted.");
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

// ── PR-09 Typed MIR Builder tests ─────────────────────────────────────────────

static void MirBuilderBuildsTypedMirForSample()
{
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var source = File.ReadAllText(samplePath);
    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    AssertTrue(mirResult.Diagnostics.Count == 0,
        $"MIR build should produce no diagnostics for valid sample; got: {string.Join(", ", mirResult.Diagnostics.Select(d => d.Message))}");

    var mirDoc = mirResult.Document;
    AssertTrue(mirDoc is not null, "MIR document should not be null.");
    AssertTrue(mirDoc!.Functions.Count > 0, "MIR document should have at least one function.");
    AssertTrue(mirDoc.Module is not null || mirDoc.Intent is not null, "MIR document should carry module or intent.");
}

static void MirBuilderTypesAllExpressions()
{
    const string source = """
        intent "MIR typed expressions"
        fn compute(x: int) -> int
        {
            let y: int = 42
            return y
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    AssertTrue(mirResult.Diagnostics.Count == 0, "No MIR diagnostics expected.");

    var fn = mirResult.Document.Functions.Single();
    var letStmt = fn.Body.OfType<MirLetStatement>().First();
    AssertTrue(letStmt.Initializer.Type is not UnknownAIType and not null,
        $"MIR let initializer should have a typed expression, got {letStmt.Initializer.Type}.");

    var retStmt = fn.Body.OfType<MirReturnStatement>().First();
    AssertTrue(retStmt.Expression.Type is not UnknownAIType and not null,
        $"MIR return expression should have a typed expression, got {retStmt.Expression.Type}.");
}

static void MirBuilderIncludesEffectObligations()
{
    const string source = """
        intent "MIR effects"
        schema Summary = { text: string }
        fn callAi(prompt: string) -> string
            effects [model]
        {
            return model local "llama3"
                .task("summarize")
                .input(prompt)
                .output<Summary>()
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var fn = mirResult.Document.Functions.Single();
    AssertTrue(fn.DeclaredEffects.Contains("model"),
        "MIR function should carry declared 'model' effect.");
    AssertTrue(fn.EffectObligations.Count > 0,
        "MIR function with model calls should have at least one effect obligation.");
    var modelObligation = fn.EffectObligations.FirstOrDefault(o => o.Effect == "model");
    AssertTrue(modelObligation is not null, "Should have a 'model' effect obligation from model call.");
}

static void MirBuilderIncludesContractCheckPlans()
{
    const string source = """
        intent "MIR contracts"
        fn process(x: int) -> int
            requires x > 0
            ensures result > 0
        {
            return x
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    AssertTrue(mirResult.Diagnostics.Count == 0, "No MIR diagnostics expected for valid contracts.");

    var fn = mirResult.Document.Functions.Single();
    AssertTrue(fn.ContractCheckPlans.Count == 2, $"Expected 2 contract check plans, got {fn.ContractCheckPlans.Count}.");

    var pre = fn.ContractCheckPlans.Single(p => p.Kind == ContractKind.Requires);
    AssertTrue(pre.Expression == "x > 0", $"Requires expression mismatch: {pre.Expression}.");
    AssertTrue(pre.AvailableNames.Contains("x"), "Requires plan should list parameter 'x' as available.");

    var post = fn.ContractCheckPlans.Single(p => p.Kind == ContractKind.Ensures);
    AssertTrue(post.Expression == "result > 0", $"Ensures expression mismatch: {post.Expression}.");
    AssertTrue(post.AvailableNames.Contains("result"), "Ensures plan should include 'result' in available names.");
}

static void MirBuilderIncludesToolModelActorHandlerAndTestNodes()
{
    const string source = """
        intent "MIR declarations"
        capability Net
        schema Summary = { text: string }
        tool fetch(url: string) -> string
            sandbox = wasm
            capabilities = [Net]
            timeout = 5s
        fn summarize(prompt: string) -> Summary
            effects [model]
            requires prompt != empty
        {
            return model local "llama3"
                .task("summarize")
                .input(prompt)
                .output<Summary>()
        }
        actor Worker {
            state current: string
            async fn handle(job: string) -> Summary
                effects [model]
                requires job != empty
            {
                return summarize(job)
            }
        }
        test "summarizes text" {
            expect summarize("hello").text == "hello"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult, semanticResult);

    AssertTrue(mirResult.Diagnostics.Count == 0,
        $"Expected valid MIR declaration sample, got: {string.Join("; ", mirResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");

    var tool = mirResult.Document.Tools.Single();
    AssertTrue(tool is MirTool, "MIR document should expose tool declarations as MirTool nodes.");
    AssertTrue(tool.Parameters.Single().Type is PrimitiveAIType { Name: "string" }, "MIR tool parameter should be typed.");
    AssertTrue(tool.ReturnType is PrimitiveAIType { Name: "string" }, "MIR tool return type should be typed.");

    var fn = mirResult.Document.Functions.Single();
    var modelCall = fn.ModelCalls.Single();
    AssertTrue(modelCall is MirModelCall, "MIR function should expose model calls as MirModelCall nodes.");
    AssertTrue(modelCall.OutputType is RecordAIType { Name: "Summary" }, $"Model output should be typed as Summary, got {modelCall.OutputType}.");

    var handler = mirResult.Document.Actors.Single().Handlers.Single();
    AssertTrue(handler.Id.Value == "actor:Worker:handler:handle", $"Actor handler MIR id mismatch: {handler.Id.Value}.");
    AssertTrue(handler.IsAsync, "Actor handler async marker should be preserved in MIR.");

    var test = mirResult.Document.Tests.Single();
    AssertTrue(test.Name == "summarizes text", $"MIR test name mismatch: {test.Name}.");
    AssertTrue(test.Id.Value == "test:summarizes text", $"MIR test stable id mismatch: {test.Id.Value}.");
    AssertTrue(test.Location.Line > 0, "MIR test should preserve source location.");
}

static void MirBuilderUsesExplicitErrorTypeForUnresolvedExpressions()
{
    const string source = """
        intent "MIR error expression"
        fn bad() -> int
            requires true
        {
            return missingValue
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult, semanticResult);

    var ret = mirResult.Document.Functions.Single().Body.OfType<MirReturnStatement>().Single();
    AssertTrue(ret.Expression.Type is ErrorAIType,
        $"Unresolved MIR expression should use explicit error type, got {ret.Expression.Type}.");
}

static void MirBuilderRejectsInvalidProgramsBeforeLowering()
{
    // A document with a type mismatch should propagate diagnostics in the MIR build result.
    // The MIR builder re-uses existing semantic diagnostics, so we verify the result carries
    // those diagnostics and still produces a (partial) MIR for error-recovery.
    const string source = """
        intent "MIR error recovery"
        fn bad() -> int
        {
            return "not an int"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult, semanticResult);

    // The MIR builder should carry forward type-check diagnostics.
    var typeDiags = mirResult.Diagnostics.Where(d => d.Code == "AI3020").ToList();
    AssertTrue(typeDiags.Count > 0,
        "MIR build should carry AI3020 (return-type mismatch) diagnostics from semantic analysis.");

    // MIR document is still produced for error recovery.
    AssertTrue(mirResult.Document is not null, "MIR document should still be produced in error-recovery mode.");
}

static void MirBuilderPreservesSourceMapLinks()
{
    const string source = """
        intent "MIR source map"
        fn echo(x: string) -> string
        {
            return x
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    AssertTrue(mirResult.SourceMap.ContainsKey("fn:echo"),
        "MIR source map should contain 'fn:echo'.");
    var loc = mirResult.SourceMap["fn:echo"];
    AssertTrue(loc.Line > 0, "MIR source map location should have a positive line.");

    var fn = mirResult.Document.Functions.Single();
    AssertTrue(fn.Location.Line > 0, "MIR function should have a valid source location.");
}

// ─── PR-10: AISharp.Lowering Project ─────────────────────────────────────────

static void LoweringGeneratesCSharpForMinimalFunction()
{
    const string source = """
        intent "Greeter"
        fn greet(name: string) -> string
        {
            let msg = "hello"
            return msg
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);

    AssertTrue(!string.IsNullOrWhiteSpace(lowerResult.GeneratedCSharpSource),
        "Generated C# source should not be empty.");
}

static void LoweringOutputContainsStableNamespaceAndClass()
{
    const string source = """
        intent "Orders"
        fn list() -> string
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("namespace Orders"), $"Generated C# should contain 'namespace Orders'. Got: {cs}");
    AssertTrue(cs.Contains("public static class AISharpModule"),
        $"Generated C# should contain 'public static class AISharpModule'. Got: {cs}");
}

static void LoweringOutputContainsFunctionSignature()
{
    const string source = """
        intent "Math"
        fn add(a: int, b: int) -> int
        {
            return a
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("add"), $"Generated C# should contain function name 'add'. Got: {cs}");
    AssertTrue(cs.Contains("public static"), "Generated C# function should be public static.");
}

static void LoweringOutputIsDeterministic()
{
    const string source = """
        intent "Repeat"
        fn run() -> string
        {
            return "done"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var first  = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;
    var second = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(first == second, "Lowering output should be deterministic across repeated calls.");
}

static void LoweringHandlesMultipleFunctions()
{
    const string source = """
        intent "Multi"
        fn foo() -> string
        {
            return "foo"
        }
        fn bar() -> string
        {
            return "bar"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("foo"), $"Generated C# should contain function 'foo'. Got: {cs}");
    AssertTrue(cs.Contains("bar"), $"Generated C# should contain function 'bar'. Got: {cs}");
}

static void LoweringResultExposesSourceMap()
{
    const string source = """
        intent "SourceMap"
        fn ping() -> string
        {
            return "pong"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);

    AssertTrue(lowerResult.SourceMap.ContainsKey("fn:ping"),
        "Lowering source map should contain entry 'fn:ping'.");
}

static void LoweringEmitsCSharpRecordTypeDeclaration()
{
    const string source = """
        intent "Records"
        type Point = { x: int, y: int }
        fn origin() -> string
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("sealed record Point"),
        $"Generated C# should contain 'sealed record Point'. Got:\n{cs}");
    AssertTrue(cs.Contains("int x") || cs.Contains("int x,") || cs.Contains("int x)"),
        $"Generated C# record should contain field 'int x'. Got:\n{cs}");
    AssertTrue(cs.Contains("int y") || cs.Contains("int y,") || cs.Contains("int y)"),
        $"Generated C# record should contain field 'int y'. Got:\n{cs}");
}

static void LoweringEmitsCSharpUnionTypeHierarchy()
{
    const string source = """
        intent "Unions"
        type Status = Active | Archived | Pending
        fn check() -> string
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("abstract record Status"),
        $"Generated C# should contain 'abstract record Status'. Got:\n{cs}");
    AssertTrue(cs.Contains("sealed record Active()"),
        $"Generated C# should contain 'sealed record Active()'. Got:\n{cs}");
    AssertTrue(cs.Contains(": Status"),
        $"Generated C# union cases should inherit from 'Status'. Got:\n{cs}");
}

static void LoweringGeneratedCSharpCompiles()
{
    const string source = """
        intent "CompileTest"
        type Shape = Circle | Square | Triangle
        type Point = { x: int, y: int }
        fn makePoint(x: int, y: int) -> Point
        {
            return Point(x, y)
        }
        fn defaultShape() -> Shape
        {
            return Circle
        }
        fn echoPoint(point: Point) -> Point
        {
            return point
        }
        fn area(width: int, height: int) -> int
        {
            return width
        }
        fn greet(name: string) -> string
        {
            let msg = name
            return msg
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);

    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var cs = lowerResult.GeneratedCSharpSource;
    var repeat = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs == repeat, "Generated C# should be deterministic for records, unions, and calls.");
    AssertTrue(cs.Contains("return new Point(x, y);", StringComparison.Ordinal),
        $"Generated C# should lower AI# record construction to C# object construction. Got:\n{cs}");
    AssertTrue(cs.Contains("return new Shape.Circle();", StringComparison.Ordinal),
        $"Generated C# should lower AI# union cases to nested C# case construction. Got:\n{cs}");
    AssertTrue(cs.Contains("return point;", StringComparison.Ordinal),
        $"Generated C# should preserve simple variable return expressions. Got:\n{cs}");

    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-compile-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), cs);
        File.WriteAllText(Path.Combine(tmpDir, "compile-test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Library</OutputType>
              </PropertyGroup>
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {Quote(Path.Combine(tmpDir, "compile-test.csproj"))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0,
            $"Generated C# should compile. Exit {process.ExitCode}.\nOutput: {stdout}\nSTDERR: {stderr}\n\nGenerated:\n{cs}");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}


static void LoweringEmitsRequiresGuardAtFunctionEntry()
{
    // A function with a 'requires' contract should have an entry helper call before the body.
    const string source = """
        intent "Contract lowering"
        fn absolute(n: int) -> int
            requires n != 0
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("// AI# requires: n != 0", StringComparison.Ordinal),
        $"Expected requires comment in generated C#. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpContracts.Requires(n != 0, \"n != 0\", 3, 5,", StringComparison.Ordinal),
        $"Expected requires helper call with source location in generated C#. Got:\n{cs}");
    AssertTrue(cs.Contains("throw new AISharpContractException", StringComparison.Ordinal),
        $"Expected stable AISharpContractException throw path in generated C#. Got:\n{cs}");
    // Guard must appear before the return statement.
    var guardIdx  = cs.IndexOf("AISharpContracts.Requires(n != 0", StringComparison.Ordinal);
    var returnIdx = cs.IndexOf("return n;", StringComparison.Ordinal);
    AssertTrue(guardIdx >= 0 && returnIdx >= 0 && guardIdx < returnIdx,
        $"requires guard must precede the return statement. guardIdx={guardIdx}, returnIdx={returnIdx}.\n{cs}");
}

static void LoweringEmitsEnsuresGuardBeforeReturn()
{
    // A function with an 'ensures' contract should capture return value and check it
    // before returning.
    const string source = """
        intent "Contract lowering"
        fn positive(n: int) -> int
            requires true
            ensures result != 0
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("var __result =", StringComparison.Ordinal),
        $"Expected '__result' capture variable for ensures. Got:\n{cs}");
    AssertTrue(cs.Contains("// AI# ensures: result != 0", StringComparison.Ordinal),
        $"Expected ensures comment in generated C#. Got:\n{cs}");
    // 'result' in expression should be replaced with '__result'.
    AssertTrue(cs.Contains("AISharpContracts.Ensures(__result != 0, \"result != 0\", 4, 5,", StringComparison.Ordinal),
        $"Expected ensures helper call with substituted result and source location. Got:\n{cs}");
    AssertTrue(cs.Contains("return __result;", StringComparison.Ordinal),
        $"Expected 'return __result;' after ensures check. Got:\n{cs}");
    // Ensures check must appear before the final return.
    var guardIdx  = cs.IndexOf("AISharpContracts.Ensures(__result != 0", StringComparison.Ordinal);
    var returnIdx = cs.IndexOf("return __result;", StringComparison.Ordinal);
    AssertTrue(guardIdx >= 0 && returnIdx >= 0 && guardIdx < returnIdx,
        $"ensures guard must precede 'return __result;'. guardIdx={guardIdx}, returnIdx={returnIdx}.\n{cs}");
}

static void LoweringContractGuardsCompile()
{
    // Generated C# with requires and ensures guards must be valid compilable C#.
    const string source = """
        intent "ContractCompileTest"
        fn clamp(value: int, lo: int, hi: int) -> int
            requires lo != hi
            ensures result != 0
        {
            return value
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-contract-compile-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), cs);
        File.WriteAllText(Path.Combine(tmpDir, "contract-compile-test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Library</OutputType>
              </PropertyGroup>
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {Quote(Path.Combine(tmpDir, "contract-compile-test.csproj"))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0,
            $"Generated C# with contract guards should compile. Exit {process.ExitCode}.\nOutput: {stdout}\nSTDERR: {stderr}\n\nGenerated:\n{cs}");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

static void LoweringEmitsContractHelperCallsWithSourceLocations()
{
    const string source = """
        intent "LocatedContracts"
        fn identity(n: int) -> int
            requires n != 0
            ensures result != 0
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("public sealed class AISharpContractException", StringComparison.Ordinal),
        $"Generated C# should include a stable contract exception type. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpContracts.Requires(n != 0, \"n != 0\", 3, 5,", StringComparison.Ordinal),
        $"Requires should lower to helper call with source line and column. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpContracts.Ensures(__result != 0, \"result != 0\", 4, 5,", StringComparison.Ordinal),
        $"Ensures should lower to helper call with source line and column. Got:\n{cs}");
}

static void LoweringSupportsDebugOnlyContractChecks()
{
    var location = new SourceLocation(Line: 7, Column: 9, Offset: 42);
    var fn = new MirFunction(
        new HirId("fn:debugOnly"),
        "debugOnly",
        [new HirParameter("n", new PrimitiveAIType("int"), ParameterOwnership.Unspecified, location)],
        new PrimitiveAIType("int"),
        [],
        [],
        [new MirContractCheckPlan(ContractKind.Requires, "n != 0", ["n"], MirContractCheckMode.Debug, location)],
        [new MirReturnStatement(new MirExpression("n", new PrimitiveAIType("int"), location), location)],
        [],
        IsAsync: false,
        location);
    var document = new MirDocument(
        new HirId("doc:debug"),
        Module: "DebugContracts",
        Intent: null,
        Types: [],
        Schemas: [],
        Tools: [],
        Functions: [fn],
        Actors: [],
        Tests: [],
        location);

    var cs = AISharpLowering.Lower(document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("#if DEBUG", StringComparison.Ordinal),
        $"Debug contract check should be guarded by #if DEBUG. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpContracts.Requires(n != 0, \"n != 0\", 7, 9, 42);", StringComparison.Ordinal),
        $"Debug contract check should still use the runtime helper with source location. Got:\n{cs}");
}

static void LoweringEmitsInvariantAfterReturnValueIsAvailable()
{
    const string source = """
        intent "InvariantContracts"
        fn nonzero(n: int) -> int
            requires true
            invariant result != 0
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("var __result = n;", StringComparison.Ordinal),
        $"Invariant that references result should be checked after return value capture. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpContracts.Invariant(__result != 0, \"result != 0\", 4, 5,", StringComparison.Ordinal),
        $"Invariant should lower to an invariant helper call with result substituted and source location. Got:\n{cs}");

    var captureIdx = cs.IndexOf("var __result = n;", StringComparison.Ordinal);
    var invariantIdx = cs.IndexOf("AISharpContracts.Invariant(__result != 0", StringComparison.Ordinal);
    var returnIdx = cs.IndexOf("return __result;", StringComparison.Ordinal);
    AssertTrue(captureIdx >= 0 && invariantIdx > captureIdx && returnIdx > invariantIdx,
        $"Invariant must run after result capture and before return. captureIdx={captureIdx}, invariantIdx={invariantIdx}, returnIdx={returnIdx}.\n{cs}");
}

static void LoweringGeneratedContractChecksPassAndFailWithStableException()
{
    const string source = """
        intent "RuntimeContract"
        fn identity(n: int) -> int
            requires n != 0
            ensures result != 0
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    var program = """
        var passing = RuntimeContract.AISharpModule.identity(1);
        if (passing != 1)
            return 10;

        try
        {
            RuntimeContract.AISharpModule.identity(0);
            return 20;
        }
        catch (RuntimeContract.AISharpContractException ex)
            when (ex.ContractKind == "requires"
                && ex.Expression == "n != 0"
                && ex.Line == 3
                && ex.Column == 5
                && ex.Offset >= 0)
        {
            return 0;
        }
        """;

    RunGeneratedContractProgram(cs, program);
}

static void LoweringEmitsCSharpSwitchExpressionForMatchStatement()
{
    const string source = """
        intent "switch lowering"
        type Status = Active | Archived
        fn describe(status: Status) -> string
        {
            match status {
                Active => "active"
                Archived => "archived"
            }
            return "done"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("status switch"), $"Should emit switch expression on scrutinee. Got:\n{cs}");
    AssertTrue(cs.Contains("Status.Active"), $"Should emit Status.Active type pattern. Got:\n{cs}");
    AssertTrue(cs.Contains("Status.Archived"), $"Should emit Status.Archived type pattern. Got:\n{cs}");
    AssertTrue(cs.Contains("\"active\""), $"Should emit Active arm body. Got:\n{cs}");
    AssertTrue(cs.Contains("\"archived\""), $"Should emit Archived arm body. Got:\n{cs}");
    AssertTrue(cs.Contains("_ = status switch"), $"Should capture match via discard assignment. Got:\n{cs}");
}

static void LoweringEmitsWildcardArmInSwitchExpression()
{
    const string source = """
        intent "wildcard lowering"
        type Status = Active | Archived | Pending
        fn describe(status: Status) -> string
        {
            match status {
                Active => "active"
                _ => "other"
            }
            return "done"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("status switch"), $"Should emit switch expression. Got:\n{cs}");
    AssertTrue(cs.Contains("Status.Active"), $"Should emit named case pattern. Got:\n{cs}");
    AssertTrue(cs.Contains("_ =>"), $"Should emit wildcard arm. Got:\n{cs}");
    // When a wildcard arm is present no extra '_ => default' fallback should be added.
    var wildcardCount = System.Text.RegularExpressions.Regex.Matches(cs, @"\b_ =>").Count;
    AssertTrue(wildcardCount == 1, $"Expected exactly 1 wildcard arm (no duplicate default), got {wildcardCount}. Source:\n{cs}");
}

static void LoweringMatchStatementCSharpCompiles()
{
    const string source = """
        intent "match compile test"
        type Color = Red | Green | Blue
        fn colorName(c: Color) -> string
        {
            match c {
                Red => "red"
                Green => "green"
                Blue => "blue"
            }
            return "done"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-match-lower-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), cs);
        File.WriteAllText(Path.Combine(tmpDir, "match-compile-test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        using var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {Quote(Path.Combine(tmpDir, "match-compile-test.csproj"))} --nologo -v:q",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start build process.");

        var stdout = build.StandardOutput.ReadToEnd();
        var stderr = build.StandardError.ReadToEnd();
        build.WaitForExit();

        AssertTrue(build.ExitCode == 0,
            $"Match lowering generated invalid C#. Exit {build.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}\n\nGenerated:\n{cs}");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

// ─── PR-14: Async/Actor lowering ──────────────────────────────────────────────

static void LoweringEmitsAsyncTaskReturnForAsyncFunction()
{
    const string source = """
        intent "async test"
        async fn fetchName(id: string) -> string
        {
            return id
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("async Task<string>", StringComparison.Ordinal),
        $"Async fn with non-unit return should emit 'async Task<T>'. Got:\n{cs}");
    AssertTrue(cs.Contains("public static async Task<string> fetchName", StringComparison.Ordinal),
        $"Async fn should emit 'public static async Task<T> name'. Got:\n{cs}");
}

static void LoweringEmitsAsyncTaskForAsyncUnitFunction()
{
    const string source = """
        intent "async unit test"
        async fn doWork() -> unit
        {
            return unit
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("public static async Task doWork()", StringComparison.Ordinal),
        $"Async fn with unit return should emit 'public static async Task name()'. Got:\n{cs}");
    AssertTrue(!cs.Contains("async Task<void>", StringComparison.Ordinal),
        $"Async unit fn must not emit 'async Task<void>'. Got:\n{cs}");
}

static void LoweringEmitsActorClassWithStateAndHandlers()
{
    const string source = """
        intent "actor test"
        actor Counter {
            state count: int
            async fn increment() -> unit
            {
                return unit
            }
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("public sealed class Counter", StringComparison.Ordinal),
        $"Actor should emit a sealed class. Got:\n{cs}");
    AssertTrue(cs.Contains("private int count", StringComparison.Ordinal),
        $"Actor state member should emit as private field. Got:\n{cs}");
    AssertTrue(cs.Contains("public async Task increment()", StringComparison.Ordinal),
        $"Actor handler should emit as 'public async Task'. Got:\n{cs}");
}

static void LoweringEmitsSpawnAndAllAsTaskWhenAll()
{
    const string source = """
        intent "concurrency test"
        async fn loadDashboard(id: string) -> string
        {
            taskgroup work {
                spawn profile = fetchProfile(id)
                spawn orders = fetchOrders(id)
                all profile, orders
            }
            return id
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    AssertTrue(cs.Contains("var profile = fetchProfile(id);", StringComparison.Ordinal),
        $"Spawn should emit 'var name = expression;'. Got:\n{cs}");
    AssertTrue(cs.Contains("var orders = fetchOrders(id);", StringComparison.Ordinal),
        $"Second spawn should emit 'var name = expression;'. Got:\n{cs}");
    AssertTrue(cs.Contains("await Task.WhenAll(profile, orders);", StringComparison.Ordinal),
        $"All statement should emit 'await Task.WhenAll(...)'. Got:\n{cs}");
}

static void LoweringAsyncActorCSharpCompiles()
{
    const string source = """
        intent "compile actor test"
        actor Greeter {
            state greeting: string
            async fn sayHello(name: string) -> string
            {
                return name
            }
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var cs             = AISharpLowering.Lower(mirResult.Document).GeneratedCSharpSource;

    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-actor-compile-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), cs);
        File.WriteAllText(Path.Combine(tmpDir, "actor-compile-test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        using var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build {Quote(Path.Combine(tmpDir, "actor-compile-test.csproj"))} --nologo -v:q",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start build process.");

        var stdout = build.StandardOutput.ReadToEnd();
        var stderr = build.StandardError.ReadToEnd();
        build.WaitForExit();

        AssertTrue(build.ExitCode == 0,
            $"Actor lowering generated invalid C#. Exit {build.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}\n\nGenerated:\n{cs}");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

static void RunGeneratedContractProgram(string generatedCSharp, string programCSharp)
{
    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-contract-runtime-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), generatedCSharp);
        File.WriteAllText(Path.Combine(tmpDir, "Program.cs"), programCSharp);
        File.WriteAllText(Path.Combine(tmpDir, "runtime-contract-test.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project {Quote(Path.Combine(tmpDir, "runtime-contract-test.csproj"))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet run process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0,
            $"Generated C# contract runtime behavior failed. Exit {process.ExitCode}.\nOutput: {stdout}\nSTDERR: {stderr}\n\nGenerated:\n{generatedCSharp}\n\nProgram:\n{programCSharp}");
    }
    finally
    {
        Directory.Delete(tmpDir, recursive: true);
    }
}

// ─── PR-15 Runtime tests ──────────────────────────────────────────────────────

static void RuntimeContractsPassForValidPrecondition()
{
    AISharpContracts.Requires(true, "x > 0", 1, 1, 0);
    AISharpContracts.Ensures(true, "result > 0", 2, 1, 10);
    AISharpContracts.Invariant(true, "count >= 0", 3, 1, 20);
}

static void RuntimeContractsFailWithStableExceptionForViolatedPrecondition()
{
    try
    {
        AISharpContracts.Requires(false, "order.budget > 0", 10, 5, 200);
        throw new Exception("Expected AISharpContractException to be thrown.");
    }
    catch (AISharpContractException ex)
    {
        AssertEqual("requires", ex.ContractKind);
        AssertEqual("order.budget > 0", ex.Expression);
        AssertEqual(10, ex.Line);
        AssertEqual(5, ex.Column);
        AssertEqual(200, ex.Offset);
        AssertTrue(ex.Message.Contains("requires"), $"Exception message missing 'requires': {ex.Message}");
    }
}

static void RuntimeContractsFailForViolatedPostcondition()
{
    try
    {
        AISharpContracts.Ensures(false, "result.is_ok()", 20, 3, 400);
        throw new Exception("Expected AISharpContractException to be thrown.");
    }
    catch (AISharpContractException ex)
    {
        AssertEqual("ensures", ex.ContractKind);
        AssertEqual("result.is_ok()", ex.Expression);
    }
}

static void RuntimeEffectsAllowPermittedEffectKinds()
{
    var scope = EffectRuntimeContext.For(EffectKind.Model | EffectKind.Network);
    AssertTrue(scope.IsAllowed(EffectKind.Model), "model should be allowed");
    AssertTrue(scope.IsAllowed(EffectKind.Network), "network should be allowed");
    AssertFalse(scope.IsAllowed(EffectKind.Io), "io should not be allowed");

    // Require should not throw for allowed effects
    EffectRuntimeContext.Require(scope, EffectKind.Model);
    EffectRuntimeContext.Require(scope, EffectKind.Network);
}

static void RuntimeEffectsRejectUnpermittedEffectKinds()
{
    var scope = EffectRuntimeContext.For(EffectKind.Model);
    try
    {
        EffectRuntimeContext.Require(scope, EffectKind.Io);
        throw new Exception("Expected EffectScopeViolationException.");
    }
    catch (EffectScopeViolationException ex)
    {
        AssertTrue(ex.Attempted == EffectKind.Io, $"Expected Io, got {ex.Attempted}");
        AssertTrue(ex.Allowed == EffectKind.Model, $"Expected Model, got {ex.Allowed}");
    }
}

static void RuntimeEffectsPureScopeDisallowsAllEffects()
{
    var pure = EffectRuntimeContext.Pure;
    AssertFalse(pure.IsAllowed(EffectKind.Io), "pure scope should disallow io");
    AssertFalse(pure.IsAllowed(EffectKind.Model), "pure scope should disallow model");
    AssertFalse(pure.IsAllowed(EffectKind.Network), "pure scope should disallow network");
}

static void RuntimeEffectsPushAndDisposeRestoresPreviousScope()
{
    // Default ambient scope is Unrestricted; Push must narrow it and Dispose must restore.
    var beforeAllowed = EffectRuntimeContext.Current.Allowed;
    using (EffectRuntimeContext.Push(EffectRuntimeContext.For(EffectKind.Model)))
    {
        AssertTrue(EffectRuntimeContext.Current.IsAllowed(EffectKind.Model), "Pushed scope must allow Model.");
        AssertFalse(EffectRuntimeContext.Current.IsAllowed(EffectKind.Network), "Pushed scope must deny Network.");
    }
    AssertTrue(EffectRuntimeContext.Current.Allowed == beforeAllowed,
        "Disposing the pushed scope must restore the previous ambient scope.");
}

static void RuntimeEffectsRequireCurrentRejectsDeniedEffect()
{
    using (EffectRuntimeContext.Push(EffectRuntimeContext.For(EffectKind.Io)))
    {
        // Allowed effect — must not throw.
        EffectRuntimeContext.RequireCurrent(EffectKind.Io);
        try
        {
            EffectRuntimeContext.RequireCurrent(EffectKind.Network);
            throw new Exception("Expected EffectScopeViolationException for denied Network effect.");
        }
        catch (EffectScopeViolationException ex)
        {
            AssertTrue(ex.Attempted == EffectKind.Network, $"Expected Network attempted, got {ex.Attempted}.");
            AssertTrue(ex.Allowed == EffectKind.Io, $"Expected only Io allowed, got {ex.Allowed}.");
        }
    }
}

static void RuntimeEffectsKindsParseMapsAISharpNames()
{
    AssertTrue(EffectKinds.TryParse("model", out var m) && m == EffectKind.Model, "'model' must map to EffectKind.Model.");
    AssertTrue(EffectKinds.TryParse("network", out var n1) && n1 == EffectKind.Network, "'network' must map to Network.");
    AssertTrue(EffectKinds.TryParse("net", out var n2) && n2 == EffectKind.Network, "'net' alias must map to Network.");
    AssertTrue(EffectKinds.TryParse("io", out var io) && io == EffectKind.Io, "'io' must map to Io.");
    AssertTrue(EffectKinds.TryParse("time", out var t) && t == EffectKind.Time, "'time' must map to Time.");
    AssertTrue(EffectKinds.TryParse("random", out var r) && r == EffectKind.Random, "'random' must map to Random.");
    AssertTrue(EffectKinds.TryParse("unsafe_ffi", out var u) && u == EffectKind.UnsafeFfi, "'unsafe_ffi' must map to UnsafeFfi.");
    AssertFalse(EffectKinds.TryParse("db", out _), "Custom effect 'db' must not map to a built-in kind.");
    AssertFalse(EffectKinds.TryParse(null, out _), "Null name must return false.");
}

static void RuntimeEffectMetadataAttributeRecordsDeclaredKinds()
{
    var attr = new EffectMetadataAttribute(EffectKind.Model | EffectKind.Network, "model,network");
    AssertTrue((attr.Declared & EffectKind.Model) == EffectKind.Model, "Declared kinds must include Model.");
    AssertTrue((attr.Declared & EffectKind.Network) == EffectKind.Network, "Declared kinds must include Network.");
    AssertTrue(attr.EffectNames == "model,network", $"EffectNames preserved verbatim, got '{attr.EffectNames}'.");
}

static void LoweringEmitsEffectMetadataAttributeForDeclaredEffects()
{
    const string source = """
        intent "Effect metadata"
        fn call() -> string
            effects [model, network, db]
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("[AISharpEffectMetadataAttribute(AISharpEffectKind.Model | AISharpEffectKind.Network, \"model,network,db\")]", StringComparison.Ordinal),
        $"Generated C# should emit effect metadata attribute combining built-in flags and preserving custom names. Got:\n{cs}");
}

static void LoweringEmitsEffectGuardForModelFunction()
{
    const string source = """
        intent "Effect guard"
        fn ask() -> string
            effects [model]
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("AISharpEffects.Require(AISharpEffectKind.Model, \"model\");", StringComparison.Ordinal),
        $"Generated C# should emit a runtime effect guard for the model effect. Got:\n{cs}");
    // Guard must precede the return so a denied scope cannot perform the model op.
    var guardIdx  = cs.IndexOf("AISharpEffects.Require(AISharpEffectKind.Model", StringComparison.Ordinal);
    var returnIdx = cs.IndexOf("return \"ok\";", StringComparison.Ordinal);
    AssertTrue(guardIdx >= 0 && returnIdx >= 0 && guardIdx < returnIdx,
        $"Effect guard must precede the function body. guardIdx={guardIdx}, returnIdx={returnIdx}.\n{cs}");
}

static void LoweringEmitsInlineEffectRuntimeSupport()
{
    const string source = """
        intent "Inline effect runtime"
        fn ping() -> string
        {
            return "ok"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("public enum AISharpEffectKind", StringComparison.Ordinal),
        "Generated C# should emit inline AISharpEffectKind enum so the assembly is self-contained.");
    AssertTrue(cs.Contains("public static class AISharpEffects", StringComparison.Ordinal),
        "Generated C# should emit inline AISharpEffects helper class.");
    AssertTrue(cs.Contains("public static global::System.IDisposable Push(AISharpEffectKind allowed)", StringComparison.Ordinal),
        "AISharpEffects.Push should be available so hosts can narrow the ambient scope.");
    AssertTrue(cs.Contains("throw new AISharpEffectScopeViolationException", StringComparison.Ordinal),
        "Generated effect helper must throw a stable diagnostic type on policy violation.");
}

static void LoweringEmitsEffectMetadataNoneForPureFunction()
{
    const string source = """
        intent "Pure"
        fn double(n: int) -> int
        {
            return n
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("[AISharpEffectMetadataAttribute(AISharpEffectKind.None, \"\")]", StringComparison.Ordinal),
        $"Pure functions should still carry effect metadata declaring no effects. Got:\n{cs}");
    AssertFalse(cs.Contains("AISharpEffects.Require(", StringComparison.Ordinal),
        $"Pure functions must not emit runtime effect guards. Got:\n{cs}");
}

static void LoweringEmitsEffectGuardForActorHandler()
{
    const string source = """
        intent "Actor effect guard"
        actor Cache {
            state hits: u32

            async fn handle(key: string) -> string
                effects [io]
            {
                return key
            }
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    AssertTrue(cs.Contains("[AISharpEffectMetadataAttribute(AISharpEffectKind.Io, \"io\")]", StringComparison.Ordinal),
        $"Actor handlers should carry effect metadata for declared effects. Got:\n{cs}");
    AssertTrue(cs.Contains("AISharpEffects.Require(AISharpEffectKind.Io, \"io\");", StringComparison.Ordinal),
        $"Actor handlers should emit a runtime effect guard for the io effect. Got:\n{cs}");
}

static void LoweringGeneratedEffectGuardsCompileAndEnforceScope()
{
    // End-to-end: lower a function with model effect, compile the generated assembly,
    // and execute a host program that proves both allowed and denied scopes work.
    const string source = """
        intent "EffectGuardCompileTest"
        fn callModel() -> string
            effects [model]
        {
            return "ok"
        }
        fn pure() -> string
        {
            return "pure"
        }
        """;

    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    var cs             = lowerResult.GeneratedCSharpSource;

    var tmpDir = Path.Combine(Path.GetTempPath(), $"aisharp-effect-guard-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try
    {
        File.WriteAllText(Path.Combine(tmpDir, "Generated.cs"), cs);
        File.WriteAllText(Path.Combine(tmpDir, "Program.cs"), """
            using EffectGuardCompileTest;
            var allowedResult = "fail";
            using (AISharpEffects.Push(AISharpEffectKind.Model))
            {
                allowedResult = AISharpModule.callModel();
            }

            var denied = false;
            var deniedAttempted = AISharpEffectKind.None;
            var deniedAllowed = AISharpEffectKind.None;
            using (AISharpEffects.Push(AISharpEffectKind.Io))
            {
                try
                {
                    _ = AISharpModule.callModel();
                }
                catch (AISharpEffectScopeViolationException ex)
                {
                    denied = true;
                    deniedAttempted = ex.Attempted;
                    deniedAllowed = ex.Allowed;
                }
            }

            var pureResult = AISharpModule.pure();
            System.Console.WriteLine($"allowed={allowedResult};denied={denied};attempted={deniedAttempted};onlyAllowed={deniedAllowed};pure={pureResult}");
            """);
        File.WriteAllText(Path.Combine(tmpDir, "effect-guard.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <RootNamespace>EffectGuardCompileTest</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project {Quote(Path.Combine(tmpDir, "effect-guard.csproj"))} -c Release",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start dotnet run process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        AssertTrue(process.ExitCode == 0,
            $"Effect-guard host should exit 0. Exit {process.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}\nGenerated:\n{cs}");
        AssertTrue(stdout.Contains("allowed=ok", StringComparison.Ordinal),
            $"Allowed scope must permit the model effect. STDOUT: {stdout}");
        AssertTrue(stdout.Contains("denied=True", StringComparison.Ordinal),
            $"Denied scope must throw AISharpEffectScopeViolationException. STDOUT: {stdout}");
        AssertTrue(stdout.Contains("attempted=Model", StringComparison.Ordinal),
            $"Violation must record the attempted effect. STDOUT: {stdout}");
        AssertTrue(stdout.Contains("onlyAllowed=Io", StringComparison.Ordinal),
            $"Violation must record the allowed scope. STDOUT: {stdout}");
        AssertTrue(stdout.Contains("pure=pure", StringComparison.Ordinal),
            $"Pure functions must run with no effect-scope interference. STDOUT: {stdout}");
    }
    finally
    {
        try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

static void RuntimePolicyReturnsSafeDefaultsForUnknownPolicy()
{
    var ctx = new PolicyRuntimeContext();
    var policy = ctx.GetPolicy("UnknownPolicy");
    AssertEqual(InferencePolicy.LocalOnly, policy.Inference);
    AssertEqual(TrainingPolicy.Forbidden, policy.Training);
    AssertEqual(TelemetryPolicy.Forbidden, policy.Telemetry);
    AssertEqual(ClassificationLevel.Internal, policy.Classification);
}

static void RuntimePolicyEnforcesLocalOnlyInferenceByDefault()
{
    var ctx = new PolicyRuntimeContext();
    try
    {
        ctx.RequireRemoteInferenceAllowed("CustomerPII");
        throw new Exception("Expected PolicyViolationException.");
    }
    catch (PolicyViolationException ex)
    {
        AssertTrue(ex.PolicyName == "CustomerPII", $"Wrong policy name: {ex.PolicyName}");
        AssertTrue(ex.Message.Contains("remote_opt_in"), $"Missing remote_opt_in in message: {ex.Message}");
    }
}

static void RuntimePolicyAcceptsRemoteInferenceWithCorrectPolicy()
{
    var decisions = new[]
    {
        new PolicyDecision(
            "RemotePolicy",
            ClassificationLevel.Public,
            InferencePolicy.RemoteOptIn,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    };
    var ctx = new PolicyRuntimeContext(decisions);
    ctx.RequireRemoteInferenceAllowed("RemotePolicy");
    ctx.RequireExternalTextTainted("RemotePolicy");
    ctx.RequireCapabilitiesSeparated("RemotePolicy");
}

static void RuntimePolicyRejectsRemoteInferenceWithoutOptIn()
{
    var decisions = new[]
    {
        new PolicyDecision(
            "LocalPolicy",
            ClassificationLevel.Secret,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    };
    var ctx = new PolicyRuntimeContext(decisions);
    try
    {
        ctx.RequireRemoteInferenceAllowed("LocalPolicy");
        throw new Exception("Expected PolicyViolationException.");
    }
    catch (PolicyViolationException)
    {
        // Expected: local-only policy does not permit remote inference.
    }
}

static void RuntimeProvenanceStoreAppendsAndReadsRecords()
{
    var store = new InMemoryProvenanceStore();
    var record = new ModelCallProvenance(
        DateTimeOffset.UtcNow, "ollama", "llama3", "summarize",
        "abc123", "def456", "OrderPatch", false, null, null);

    store.AppendAsync(record).GetAwaiter().GetResult();
    var all = store.ReadAllAsync().GetAwaiter().GetResult();

    AssertEqual(1, all.Count);
    AssertTrue(all[0] is ModelCallProvenance mc && mc.Provider == "ollama",
        "Expected ModelCallProvenance with provider 'ollama'.");
}

static void RuntimeProvenanceStoreFiltersByKind()
{
    var store = new InMemoryProvenanceStore();
    store.AppendAsync(new ModelCallProvenance(
        DateTimeOffset.UtcNow, "ollama", "llama3", "task", null, null, null, false, null, null))
        .GetAwaiter().GetResult();
    store.AppendAsync(new SourceHashProvenance(
        DateTimeOffset.UtcNow, "orders.aisharp", "sha256:abc"))
        .GetAwaiter().GetResult();

    var modelRecords = store.ReadByKindAsync(ProvenanceRecordKind.ModelCall).GetAwaiter().GetResult();
    AssertEqual(1, modelRecords.Count);
    AssertTrue(modelRecords[0].Kind == ProvenanceRecordKind.ModelCall, "Expected ModelCall kind.");
}

static void RuntimeFileProvenanceStoreAppendsAndReadsRecords()
{
    var dir = Path.Combine(Path.GetTempPath(), $"aisharp-provenance-store-{Guid.NewGuid():N}");
    try
    {
        using var store = new FileProvenanceStore(dir);
        var record = new SourceHashProvenance(
            DateTimeOffset.UtcNow,
            "orders.aisharp",
            "sha256:source");

        store.AppendAsync(record).GetAwaiter().GetResult();
        var all = store.ReadAllAsync().GetAwaiter().GetResult();

        AssertEqual(1, all.Count);
        AssertTrue(all[0] is SourceHashProvenance source && source.Hash == "sha256:source",
            "Expected source hash provenance record to round-trip through file store.");
        AssertTrue(File.Exists(store.StorePath), "File provenance store should create its NDJSON file.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void RuntimeFileProvenanceStoreRoundTripsAllRecordKinds()
{
    var dir = Path.Combine(Path.GetTempPath(), $"aisharp-provenance-kinds-{Guid.NewGuid():N}");
    try
    {
        using var store = new FileProvenanceStore(dir);
        ProvenanceRecord[] records =
        [
            new ModelCallProvenance(DateTimeOffset.UtcNow, "local", "llama3", "summarize", "sha256:in", "sha256:out", "Summary", false, 7, 0.0),
            new ToolInvocationProvenance(DateTimeOffset.UtcNow, "lint", ["io"], "sha256:in", "sha256:out", TimeSpan.FromMilliseconds(2), false),
            new SourceHashProvenance(DateTimeOffset.UtcNow, "orders.aisharp", "sha256:source"),
            new LoweredArtifactProvenance(DateTimeOffset.UtcNow, "orders.g.cs", "sha256:artifact", "orders.aisharp"),
            new BuildCommandProvenance(DateTimeOffset.UtcNow, "dotnet build", 0, "sha256:artifact"),
            new TestCommandProvenance(DateTimeOffset.UtcNow, "dotnet test", 3, 0),
            new HumanApprovalProvenance(DateTimeOffset.UtcNow, "review", "sha256:item"),
            new PatchProposalProvenance(DateTimeOffset.UtcNow, "sha256:proposal", 2, "Medium", false)
        ];

        foreach (var record in records)
            store.AppendAsync(record).GetAwaiter().GetResult();

        var all = store.ReadAllAsync().GetAwaiter().GetResult();
        AssertEqual(records.Length, all.Count);
        foreach (var kind in Enum.GetValues<ProvenanceRecordKind>())
        {
            AssertTrue(all.Any(r => r.Kind == kind), $"Expected provenance kind '{kind}' to round-trip.");
        }
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void RuntimeFileProvenanceStoreReadsByKind()
{
    var dir = Path.Combine(Path.GetTempPath(), $"aisharp-provenance-filter-{Guid.NewGuid():N}");
    try
    {
        using var store = new FileProvenanceStore(dir);
        store.AppendAsync(new SourceHashProvenance(
            DateTimeOffset.UtcNow, "orders.aisharp", "sha256:source")).GetAwaiter().GetResult();
        store.AppendAsync(new LoweredArtifactProvenance(
            DateTimeOffset.UtcNow, "orders.g.cs", "sha256:artifact", "orders.aisharp")).GetAwaiter().GetResult();

        var lowered = store.ReadByKindAsync(ProvenanceRecordKind.LoweredArtifact).GetAwaiter().GetResult();

        AssertEqual(1, lowered.Count);
        AssertTrue(lowered[0] is LoweredArtifactProvenance artifact && artifact.ArtifactPath == "orders.g.cs",
            "Expected file store kind filter to return the lowered artifact record.");
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

static void RuntimeProvenanceSerializerRoundTripsWithoutSensitiveFields()
{
    var record = new ModelCallProvenance(
        DateTimeOffset.UtcNow,
        "local",
        "llama3",
        "summarize",
        "sha256:input",
        "sha256:output",
        "Summary",
        IsReplay: true,
        ReplaySeed: 7,
        ReplayTemperature: 0.0);

    var json = ProvenanceSerializer.Serialize(record);
    var roundTripped = ProvenanceSerializer.Deserialize(json);

    AssertTrue(roundTripped is ModelCallProvenance model && model.InputHash == "sha256:input",
        "Expected model call provenance to deserialize with hash metadata.");
    AssertTrue(json.Contains("\"kind\""), $"Serialized provenance should include kind discriminator: {json}");
    AssertFalse(json.Contains("prompt", StringComparison.OrdinalIgnoreCase),
        "Provenance JSON should not include raw prompt fields.");
    AssertFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase),
        "Provenance JSON should not include secret fields.");
}

static void CliLowerCommandEmitsProvenancePath()
{
    var sourcePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var outDir = Path.Combine(Path.GetTempPath(), $"aisharp-lower-provenance-{Guid.NewGuid():N}");

    try
    {
        var result = RunCli($"lower {Quote(sourcePath)} --out {Quote(outDir)} --json");

        AssertTrue(result.ExitCode == 0,
            $"Expected lower command to succeed. Exit {result.ExitCode}. STDOUT: {result.Stdout} STDERR: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        AssertTrue(root.TryGetProperty("provenancePath", out var pathElement),
            $"Expected lower JSON to include provenancePath. JSON: {result.Stdout}");
        var provenancePath = pathElement.GetString();
        AssertTrue(!string.IsNullOrWhiteSpace(provenancePath), "provenancePath should not be empty.");
        AssertTrue(File.Exists(provenancePath), $"Expected provenance file to exist: {provenancePath}");

        var lines = File.ReadAllLines(provenancePath!);
        AssertTrue(lines.Any(line => line.Contains("\"kind\"") && line.Contains("LoweredArtifact")),
            "Expected provenance file to contain a lowered artifact record.");
    }
    finally
    {
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
    }
}

static void CliLowerCommandPersistsSourceAndLoweredArtifactHashes()
{
    var sourcePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var outDir = Path.Combine(Path.GetTempPath(), $"aisharp-lower-hashes-{Guid.NewGuid():N}");

    try
    {
        var result = RunCli($"lower {Quote(sourcePath)} --out {Quote(outDir)} --json");

        AssertTrue(result.ExitCode == 0,
            $"Expected lower command to succeed. Exit {result.ExitCode}. STDOUT: {result.Stdout} STDERR: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        var provenancePath = json.RootElement.GetProperty("provenancePath").GetString();
        AssertTrue(!string.IsNullOrWhiteSpace(provenancePath), "provenancePath should not be empty.");

        var records = File.ReadAllLines(provenancePath!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ProvenanceSerializer.Deserialize)
            .ToArray();

        var source = records.OfType<SourceHashProvenance>().SingleOrDefault();
        AssertTrue(source is not null, "Expected CLI lower provenance to include a source hash record.");
        AssertTrue(source!.SourcePath == Path.GetFullPath(sourcePath), "Source hash record should bind to the lowered source path.");
        AssertTrue(source.Hash == ComputeSha256FileHash(sourcePath), "Source hash should match the source file content.");

        var generatedPath = Path.Combine(outDir, "orders.g.cs");
        var lowered = records.OfType<LoweredArtifactProvenance>().SingleOrDefault();
        AssertTrue(lowered is not null, "Expected CLI lower provenance to include a lowered artifact hash record.");
        AssertTrue(lowered!.ArtifactPath == Path.GetFullPath(generatedPath), "Lowered artifact record should bind to the generated C# path.");
        AssertTrue(lowered.Hash == ComputeSha256FileHash(generatedPath), "Lowered artifact hash should match the generated file content.");
    }
    finally
    {
        if (Directory.Exists(outDir))
            Directory.Delete(outDir, recursive: true);
    }
}

static string ComputeSha256FileHash(string filePath)
{
    var bytes = SHA256.HashData(File.ReadAllBytes(filePath));
    return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
}

static void RuntimeToolSandboxDeniesUndeclaredCapability()
{
    var sandbox = new FakeToolSandbox();
    sandbox.AddCapability("read_file");

    try
    {
        sandbox.InvokeAsync(new ToolRequest(
            "web_search",
            "{}",
            ["network"],
            TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        throw new Exception("Expected ToolCapabilityDeniedException.");
    }
    catch (ToolCapabilityDeniedException ex)
    {
        AssertEqual("web_search", ex.ToolName);
        AssertEqual("network", ex.Capability);
    }
}

static void RuntimeToolSandboxInvokesRegisteredTool()
{
    var sandbox = new FakeToolSandbox();
    sandbox.AddCapability("read_file");
    sandbox.Register("file_reader", req =>
        new ToolResponse(true, "{\"content\":\"hello\"}", null, false, TimeSpan.FromMilliseconds(1), null));

    var result = sandbox.InvokeAsync(new ToolRequest(
        "file_reader",
        "{\"path\":\"test.txt\"}",
        ["read_file"],
        TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();

    AssertTrue(result.Success, "Tool invocation should succeed.");
    AssertTrue(result.OutputPayload?.Contains("hello") == true, "Expected hello in output.");
}

static void RuntimeResourceScopeDisposesResourcesOnExit()
{
    var disposed = false;
    var resource = new TestDisposableResource(() => disposed = true);

    using (var scope = new ResourceScope())
    {
        scope.Track(resource);
        AssertFalse(resource.IsConsumed, "Resource should not be consumed yet.");
    }

    AssertTrue(disposed, "Resource should have been disposed when scope exited.");
}

static void RuntimeActorContextExposesIdentityAndEffectScope()
{
    var identity = new ActorIdentity("Counter", "instance-1");
    var scope = EffectRuntimeContext.For(EffectKind.Io);
    var policy = new PolicyRuntimeContext();
    var ctx = new ActorMessageContext(identity, scope, policy);

    AssertEqual("Counter", ctx.Identity.ActorType);
    AssertEqual("instance-1", ctx.Identity.InstanceId);
    AssertEqual("Counter/instance-1", ctx.Identity.ToString());
    AssertTrue(ctx.EffectScope.IsAllowed(EffectKind.Io), "Expected Io to be allowed.");
    AssertFalse(ctx.EffectScope.IsAllowed(EffectKind.Model), "Expected Model to be denied.");
}

// ─── PR-17 Model runtime tests ────────────────────────────────────────────────

static void RuntimeModelRuntimeExecutesFakeSchemaConstrainedCall()
{
    var runtime = new FakeLocalModelRuntime();
    runtime.Register("ollama", "llama3", "summarize",
        _ => new ModelResponse(true, "{\"summary\":\"ok\"}", null, "sha256:abc", null));

    var request = new ModelRequest(
        "ollama", "llama3", "summarize", "{\"text\":\"hello\"}",
        SchemaName: "SummaryOutput",
        ProviderKind: ModelProviderKind.Local,
        PolicyName: null);

    var response = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(response.Success, $"Expected success. Error: {response.ErrorMessage}");
    AssertTrue(response.OutputPayload?.Contains("summary") == true,
        $"Expected summary in output: {response.OutputPayload}");
}

static void RuntimeModelRuntimeRejectsMissingSchema()
{
    var runtime = new FakeLocalModelRuntime();
    var request = new ModelRequest(
        "ollama", "llama3", "summarize", "{}",
        SchemaName: null,
        ProviderKind: ModelProviderKind.Local,
        PolicyName: null);

    try
    {
        runtime.ExecuteAsync(request).GetAwaiter().GetResult();
        throw new Exception("Expected ArgumentException for missing schema.");
    }
    catch (ArgumentException ex)
    {
        AssertTrue(ex.Message.Contains("schema"), $"Exception should mention schema: {ex.Message}");
    }
}

static void RuntimeModelRuntimeRejectsRemoteProviderWithoutPolicy()
{
    var runtime = new FakeLocalModelRuntime();
    var request = new ModelRequest(
        "openai", "gpt-4", "summarize", "{}",
        SchemaName: "SummaryOutput",
        ProviderKind: ModelProviderKind.Remote,
        PolicyName: null);

    try
    {
        runtime.ExecuteAsync(request).GetAwaiter().GetResult();
        throw new Exception("Expected PolicyViolationException for null policy name.");
    }
    catch (PolicyViolationException)
    {
        // Expected: remote without policy name should fail.
    }
}

static void RuntimeModelRuntimeRejectsRemoteProviderWithLocalOnlyPolicy()
{
    var decisions = new[]
    {
        new PolicyDecision(
            "TightPolicy",
            ClassificationLevel.Secret,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    };
    var runtime = new FakeLocalModelRuntime(new PolicyRuntimeContext(decisions));
    var request = new ModelRequest(
        "openai", "gpt-4", "summarize", "{}",
        SchemaName: "SummaryOutput",
        ProviderKind: ModelProviderKind.Remote,
        PolicyName: "TightPolicy");

    try
    {
        runtime.ExecuteAsync(request).GetAwaiter().GetResult();
        throw new Exception("Expected PolicyViolationException for local-only policy.");
    }
    catch (PolicyViolationException ex)
    {
        AssertEqual("TightPolicy", ex.PolicyName);
    }
}

static void RuntimeModelRuntimeAcceptsRemoteProviderWithOptInPolicy()
{
    var decisions = new[]
    {
        new PolicyDecision(
            "RemotePolicy",
            ClassificationLevel.Public,
            InferencePolicy.RemoteOptIn,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    };
    var runtime = new FakeLocalModelRuntime(new PolicyRuntimeContext(decisions));
    runtime.Register("openai", "gpt-4", "summarize",
        _ => new ModelResponse(true, "{\"summary\":\"remote ok\"}", null, null, null));

    var request = new ModelRequest(
        "openai", "gpt-4", "summarize", "{}",
        SchemaName: "SummaryOutput",
        ProviderKind: ModelProviderKind.Remote,
        PolicyName: "RemotePolicy");

    var response = runtime.ExecuteAsync(request).GetAwaiter().GetResult();
    AssertTrue(response.Success, $"Expected success for remote opt-in. Error: {response.ErrorMessage}");
}

static void RuntimeModelRuntimeReportsUnregisteredHandlerClearly()
{
    var runtime = new FakeLocalModelRuntime();
    var request = new ModelRequest(
        "ollama", "llama3", "unknown-task", "{}",
        SchemaName: "AnySchema",
        ProviderKind: ModelProviderKind.Local,
        PolicyName: null);

    var response = runtime.ExecuteAsync(request).GetAwaiter().GetResult();
    AssertFalse(response.Success, "Unregistered handler should return failure.");
    AssertTrue(response.ErrorMessage?.Contains("no handler") == true,
        $"Error message should mention 'no handler': {response.ErrorMessage}");
}

static void RuntimeModelRuntimeHealthCheckReturnsTrue()
{
    var runtime = new FakeLocalModelRuntime();
    var ok = runtime.HealthCheckAsync().GetAwaiter().GetResult();
    AssertTrue(ok, "FakeLocalModelRuntime health check should always return true.");
}

// ─── PR-21 Tool sandbox tests ─────────────────────────────────────────────────

static void RuntimeToolSandboxEnforcesTimeout()
{
    var sandbox = new FakeToolSandbox();
    sandbox.AddCapability("io");
    sandbox.Register("slow_tool", _ =>
    {
        Thread.Sleep(500);
        return new ToolResponse(true, "{}", null, false, TimeSpan.Zero, null);
    });

    var request = new ToolRequest(
        "slow_tool", "{}", ["io"],
        Timeout: TimeSpan.FromMilliseconds(10));

    var response = sandbox.InvokeAsync(request).GetAwaiter().GetResult();

    AssertTrue(response.TimedOut, $"Expected TimedOut=true. Success={response.Success}, Error={response.ErrorMessage}");
    AssertFalse(response.Success, "Timed out tool should not succeed.");
    AssertTrue(response.ErrorMessage?.Contains("timed out") == true,
        $"Error message should mention timeout: {response.ErrorMessage}");
}

static void RuntimeToolSandboxCapturesProvenanceWithHashes()
{
    var sandbox = new FakeToolSandbox();
    sandbox.AddCapability("io");
    sandbox.Register("hash_tool", req =>
        new ToolResponse(true, """{"result":"ok"}""", null, false, TimeSpan.FromMilliseconds(1), null));

    var request = new ToolRequest(
        "hash_tool", """{"file":"test.txt"}""", ["io"],
        Timeout: TimeSpan.FromSeconds(5));

    var response = sandbox.InvokeAsync(request).GetAwaiter().GetResult();

    AssertTrue(response.Success, $"Expected success. Error: {response.ErrorMessage}");
    AssertTrue(response.Provenance is not null, "Expected provenance to be set.");
    AssertEqual("hash_tool", response.Provenance!.ToolName);
    AssertTrue(response.Provenance.InputHash?.StartsWith("sha256:") == true,
        $"Input hash should be SHA-256: {response.Provenance.InputHash}");
    AssertTrue(response.Provenance.OutputHash?.StartsWith("sha256:") == true,
        $"Output hash should be SHA-256: {response.Provenance.OutputHash}");
    AssertTrue(response.Provenance.GrantedCapabilities.Contains("io"),
        "Provenance should record granted capabilities.");
    AssertFalse(response.Provenance.TimedOut, "Provenance should record TimedOut=false.");
}

static void RuntimeToolSandboxValidatesOutputSchema()
{
    var registry = new SchemaRegistry();
    registry.Register(RuntimeSchema.FromFields("ToolOutput",
    [
        ("result", "string"),
        ("count",  "int"),
    ]));

    var sandbox = new FakeToolSandbox(registry);
    sandbox.AddCapability("io");
    sandbox.Register("bad_tool", _ =>
        new ToolResponse(true, """{"result":"ok"}""", null, false, TimeSpan.Zero, null));

    // Missing required 'count' field.
    var request = new ToolRequest(
        "bad_tool", "{}", ["io"],
        Timeout: TimeSpan.FromSeconds(5),
        OutputSchemaName: "ToolOutput");

    var response = sandbox.InvokeAsync(request).GetAwaiter().GetResult();
    AssertFalse(response.Success, "Expected failure when output missing required field 'count'.");
    AssertTrue(response.ErrorMessage?.Contains("schema validation") == true,
        $"Error should mention schema validation: {response.ErrorMessage}");
}

static void RuntimeToolSandboxPassesOutputWithValidSchema()
{
    var registry = new SchemaRegistry();
    registry.Register(RuntimeSchema.FromFields("ToolOutput",
    [
        ("result", "string"),
        ("count",  "int"),
    ]));

    var sandbox = new FakeToolSandbox(registry);
    sandbox.AddCapability("io");
    sandbox.Register("good_tool", _ =>
        new ToolResponse(true, """{"result":"ok","count":3}""", null, false, TimeSpan.Zero, null));

    var request = new ToolRequest(
        "good_tool", "{}", ["io"],
        Timeout: TimeSpan.FromSeconds(5),
        OutputSchemaName: "ToolOutput");

    var response = sandbox.InvokeAsync(request).GetAwaiter().GetResult();
    AssertTrue(response.Success, $"Expected success. Error: {response.ErrorMessage}");
    AssertTrue(response.Provenance is not null, "Expected provenance.");
    AssertTrue(response.Provenance!.OutputHash?.StartsWith("sha256:") == true,
        "Expected SHA-256 output hash.");
}

static void RuntimeWasmBoundaryEnforcesCapabilities()
{
    // Test that WasmToolSandboxBoundary.EnforceCapabilities works correctly
    // using a concrete test subclass.
    var boundary = new TestWasmBoundary(["read_file"]);

    try
    {
        boundary.EnforceCapabilitiesPublic("my_tool", ["write_file"]);
        throw new Exception("Expected ToolCapabilityDeniedException.");
    }
    catch (ToolCapabilityDeniedException ex)
    {
        AssertEqual("my_tool", ex.ToolName);
        AssertEqual("write_file", ex.Capability);
    }

    // Should not throw for allowed capability.
    boundary.EnforceCapabilitiesPublic("my_tool", ["read_file"]);
}

// ─── PR-20 Schema validation tests ───────────────────────────────────────────

static RuntimeSchema MakeOrderSchema() =>
    RuntimeSchema.FromFields("OrderPatch",
    [
        ("orderId", "string"),
        ("amount",  "float"),
        ("shipped", "bool"),
        ("tags",    "array"),
    ]);

static void RuntimeSchemaValidatorPassesValidPayload()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema,
        """{"orderId":"O-1","amount":99.5,"shipped":false,"tags":[]}""");
    AssertTrue(result.IsValid, $"Expected valid. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
    AssertEqual(0, result.Errors.Count);
}

static void RuntimeSchemaValidatorFailsOnMissingRequiredField()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema,
        """{"amount":10.0,"shipped":true,"tags":[]}""");
    AssertFalse(result.IsValid, "Expected validation failure for missing orderId.");
    AssertTrue(result.Errors.Any(e => e.Kind == SchemaValidationErrorKind.MissingField && e.FieldName == "orderId"),
        $"Expected MissingField error for 'orderId'. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
}

static void RuntimeSchemaValidatorFailsOnWrongType()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema,
        """{"orderId":123,"amount":"not-a-number","shipped":false,"tags":[]}""");
    AssertFalse(result.IsValid, "Expected validation failure for wrong types.");
    AssertTrue(result.Errors.Any(e => e.Kind == SchemaValidationErrorKind.WrongType && e.FieldName == "orderId"),
        "Expected WrongType error for 'orderId'.");
    AssertTrue(result.Errors.Any(e => e.Kind == SchemaValidationErrorKind.WrongType && e.FieldName == "amount"),
        "Expected WrongType error for 'amount'.");
}

static void RuntimeSchemaValidatorPassesWithExtraFieldsInDefaultMode()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema,
        """{"orderId":"O-2","amount":5.0,"shipped":true,"tags":[],"extra":"value"}""",
        SchemaValidationMode.Default);
    AssertTrue(result.IsValid,
        $"Expected extra fields to pass in Default mode. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
}

static void RuntimeSchemaValidatorRejectsExtraFieldsInStrictMode()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema,
        """{"orderId":"O-3","amount":5.0,"shipped":true,"tags":[],"extra":"value"}""",
        SchemaValidationMode.Strict);
    AssertFalse(result.IsValid, "Expected extra fields to fail in Strict mode.");
    AssertTrue(result.Errors.Any(e => e.Kind == SchemaValidationErrorKind.ExtraField && e.FieldName == "extra"),
        "Expected ExtraField error for 'extra'.");
}

static void RuntimeSchemaValidatorFailsOnInvalidJson()
{
    var schema = MakeOrderSchema();
    var result = SchemaValidator.Validate(schema, "{not valid json}");
    AssertFalse(result.IsValid, "Expected validation failure for invalid JSON.");
    AssertTrue(result.Errors.Any(e => e.Kind == SchemaValidationErrorKind.InvalidJson),
        "Expected InvalidJson error.");
}

static void RuntimeSchemaValidatorPreservesSchemaAndCallSiteLocations()
{
    var schema = RuntimeSchema.FromFields("OrderPatch",
    [
        ("orderId", "string"),
        ("amount", "float"),
    ], sourcePath: "schemas/orders.aisharp", sourceLine: 12);

    var result = SchemaValidator.Validate(schema,
        """{"orderId":123}""",
        callSitePath: "src/generated/OrderService.cs",
        callSiteLine: 44);

    AssertFalse(result.IsValid, "Expected wrong type and missing field failures.");
    var wrongType = result.Errors.Single(e => e.Kind == SchemaValidationErrorKind.WrongType);
    AssertEqual("schemas/orders.aisharp", wrongType.SourcePath, "Error should retain source schema path.");
    AssertEqual(12, wrongType.SourceLine, "Error should retain source schema line.");
    AssertEqual("src/generated/OrderService.cs", ReadStringProperty(wrongType, "CallSitePath"), "Error should retain call-site path.");
    AssertEqual(44, ReadIntProperty(wrongType, "CallSiteLine"), "Error should retain call-site line.");
}

static void RuntimeSchemaCanBeBuiltFromParsedAISharpSchemaFields()
{
    var parsed = AISharpParser.Parse("""
        schema OrderPatch = {
          orderId: string
          amount: float
          shipped: bool
          tags: list<string>
        }
        """);

    var declaration = parsed.Document.Schemas.Single();
    var runtimeSchema = RuntimeSchema.FromFields(
        declaration.Name,
        declaration.Fields.Select(field => (field.Name, field.Type)),
        sourcePath: "samples/orders.aisharp",
        sourceLine: declaration.Location.Line);

    AssertEqual("OrderPatch", runtimeSchema.Name);
    AssertEqual(RuntimeFieldKind.String, runtimeSchema.Fields[0].Kind);
    AssertEqual(RuntimeFieldKind.Float, runtimeSchema.Fields[1].Kind);
    AssertEqual(RuntimeFieldKind.Bool, runtimeSchema.Fields[2].Kind);
    AssertEqual(RuntimeFieldKind.Array, runtimeSchema.Fields[3].Kind);
    AssertEqual("samples/orders.aisharp", runtimeSchema.SourcePath);
    AssertEqual(declaration.Location.Line, runtimeSchema.SourceLine);
}

static void RuntimeSchemaRegistryValidatesByName()
{
    var registry = new SchemaRegistry();
    registry.Register(MakeOrderSchema());

    AssertTrue(registry.Contains("OrderPatch"), "Registry should contain 'OrderPatch'.");

    var result = registry.Validate("OrderPatch",
        """{"orderId":"X","amount":1.0,"shipped":false,"tags":[]}""");
    AssertTrue(result.IsValid, $"Expected valid. Errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
}

static void RuntimeSchemaRegistryThrowsForUnregisteredSchema()
{
    var registry = new SchemaRegistry();
    try
    {
        registry.Validate("NoSuchSchema", "{}");
        throw new Exception("Expected SchemaNotFoundException.");
    }
    catch (SchemaNotFoundException ex)
    {
        AssertEqual("NoSuchSchema", ex.SchemaName);
    }
}

static void RuntimeSchemaRegistryRequireValidThrowsOnFailure()
{
    var registry = new SchemaRegistry();
    registry.Register(MakeOrderSchema());
    try
    {
        registry.RequireValid("OrderPatch",
            """{"amount":1.0,"shipped":false,"tags":[]}""");
        throw new Exception("Expected SchemaValidationException.");
    }
    catch (SchemaValidationException ex)
    {
        AssertFalse(ex.Result.IsValid, "Result should be invalid.");
        AssertTrue(ex.Result.Errors.Any(e => e.FieldName == "orderId"),
            "Exception should mention 'orderId'.");
        AssertTrue(ex.Message.Contains("OrderPatch"),
            $"Exception message should mention schema name. Got: {ex.Message}");
    }
}

// ─── PR-23: AI Patch Proposal Flow tests ────────────────────────────────────

static void AiPatchProposalValidatorAcceptsValidProposal()
{
    var proposal = new PatchProposal(
        ChangedFiles: [new PatchProposalFile("src/Foo.cs", "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1 +1 @@\n-old\n+new")],
        Rationale: "Fix null-dereference in Foo.cs",
        TestsToRun: ["dotnet test --filter Foo"],
        RiskLevel: PatchProposalRiskLevel.Low,
        PolicyAcknowledgements: ["No secrets included.", "No network calls added."]);

    var result = PatchProposalValidator.Validate(proposal);

    AssertTrue(result.IsValid, $"Expected valid proposal to pass. Errors: {string.Join("; ", result.Errors)}");
    AssertTrue(result.Errors.Count == 0, "Valid proposal should produce no errors.");
    AssertFalse(string.IsNullOrWhiteSpace(proposal.ProposalHash),
        "Valid proposal should have a non-empty proposal hash.");
}

static void AiPatchProposalValidatorRejectsEmptyChangedFiles()
{
    var proposal = new PatchProposal(
        ChangedFiles: [],
        Rationale: "Fix bug",
        TestsToRun: ["dotnet test"],
        RiskLevel: PatchProposalRiskLevel.Low,
        PolicyAcknowledgements: ["No secrets."]);

    var result = PatchProposalValidator.Validate(proposal);

    AssertFalse(result.IsValid, "Proposal with no changed files should fail validation.");
    AssertTrue(result.Errors.Any(e => e.Contains("at least one changed file")),
        $"Expected 'at least one changed file' error. Errors: {string.Join("; ", result.Errors)}");
}

static void AiPatchProposalValidatorRejectsMissingRationale()
{
    var proposal = new PatchProposal(
        ChangedFiles: [new PatchProposalFile("src/Foo.cs", "@@ -1 +1 @@\n-old\n+new")],
        Rationale: "  ",
        TestsToRun: ["dotnet test"],
        RiskLevel: PatchProposalRiskLevel.Medium,
        PolicyAcknowledgements: ["No secrets."]);

    var result = PatchProposalValidator.Validate(proposal);

    AssertFalse(result.IsValid, "Proposal with blank rationale should fail validation.");
    AssertTrue(result.Errors.Any(e => e.Contains("rationale")),
        $"Expected 'rationale' error. Errors: {string.Join("; ", result.Errors)}");
}

static void AiPatchProposalHelperReturnsValidProposal()
{
    var runtime = new FakeLocalModelRuntime();
    var registry = new SchemaRegistry();
    var store = new InMemoryProvenanceStore();

    const string validPayload = """
        {
          "changedFiles": [
            { "path": "src/Orders.cs", "unifiedDiff": "--- a/src/Orders.cs\n+++ b/src/Orders.cs\n@@ -1 +1 @@\n-old\n+new" }
          ],
          "rationale": "Improve order validation logic",
          "testsToRun": ["dotnet test --filter Orders"],
          "riskLevel": "Medium",
          "policyAcknowledgements": ["No secrets included.", "No network calls added."]
        }
        """;

    runtime.Register("local", "patch-proposer", "propose-patch",
        _ => new ModelResponse(true, validPayload, null, "sha256:out", null));

    var request = new PatchProposalRequest(
        TaskDescription: "Improve order validation",
        ContextPayload: "{}",
        PolicyName: "CustomerPII");

    var result = PatchProposalHelper.RequestAsync(request, runtime, registry, store)
        .GetAwaiter().GetResult();

    AssertTrue(result.Success, $"Expected patch proposal to succeed. Error: {result.ErrorMessage}");
    AssertTrue(result.Proposal is not null, "Expected a non-null proposal.");
    AssertEqual("Improve order validation logic", result.Proposal!.Rationale);
    AssertEqual(1, result.Proposal.ChangedFiles.Count);
    AssertTrue(result.Proposal.RiskLevel == PatchProposalRiskLevel.Medium,
        "Expected risk level to be Medium.");
    AssertTrue(result.Provenance is not null, "Expected provenance record.");
    AssertFalse(string.IsNullOrWhiteSpace(result.Provenance!.ProposalHash),
        "Expected non-empty provenance hash.");
    AssertFalse(result.Provenance.WasApplied, "Proposal-only flow must not set WasApplied.");

    var stored = store.ReadAllAsync().GetAwaiter().GetResult();
    AssertEqual(1, stored.Count);
    AssertTrue(stored[0] is PatchProposalProvenance,
        "Expected provenance store to contain a PatchProposalProvenance record.");
}

static void AiPatchProposalHelperRejectsInvalidSchemaOutput()
{
    var runtime = new FakeLocalModelRuntime();
    var registry = new SchemaRegistry();

    // Payload missing required fields (no changedFiles, no rationale, etc.)
    const string badPayload = """{ "riskLevel": "Low" }""";

    runtime.Register("local", "patch-proposer", "propose-patch",
        _ => new ModelResponse(true, badPayload, null, null, null));

    var request = new PatchProposalRequest(
        TaskDescription: "Fix bug",
        ContextPayload: "{}",
        PolicyName: "CustomerPII");

    var result = PatchProposalHelper.RequestAsync(request, runtime, registry)
        .GetAwaiter().GetResult();

    AssertFalse(result.Success, "Proposal helper should reject output missing required schema fields.");
    AssertTrue(result.ErrorMessage is not null &&
               (result.ErrorMessage.Contains("Schema validation") || result.ErrorMessage.Contains("validation")),
        $"Expected schema validation error. Got: {result.ErrorMessage}");
    AssertTrue(result.Proposal is null, "No proposal should be returned on failure.");
    AssertTrue(result.Provenance is null, "No provenance should be captured on failure.");
}

static void AiPatchProposalCapturesProvenanceWithoutMutatingFiles()
{
    var runtime = new FakeLocalModelRuntime();
    var registry = new SchemaRegistry();
    var store = new InMemoryProvenanceStore();

    // Snapshot every .aisharp file hash before the call.
    var sampleDir = Path.Combine(FindRepositoryRoot(), "samples");
    var beforeHashes = Directory.GetFiles(sampleDir, "*.aisharp")
        .ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));

    const string payload = """
        {
          "changedFiles": [
            { "path": "samples/orders.aisharp", "unifiedDiff": "@@ -1 +1 @@\n-old\n+new" }
          ],
          "rationale": "Optimise allocate function",
          "testsToRun": ["dotnet test --filter Orders"],
          "riskLevel": "High",
          "policyAcknowledgements": ["Proposal-only; no files mutated."]
        }
        """;

    runtime.Register("local", "patch-proposer", "propose-patch",
        _ => new ModelResponse(true, payload, null, null, null));

    var request = new PatchProposalRequest(
        TaskDescription: "Optimise allocate",
        ContextPayload: "{}",
        PolicyName: "CustomerPII",
        SessionId: "test-session-pr23");

    var result = PatchProposalHelper.RequestAsync(request, runtime, registry, store)
        .GetAwaiter().GetResult();

    AssertTrue(result.Success, $"Expected success. Error: {result.ErrorMessage}");

    // Verify no .aisharp files were mutated.
    foreach (var (path, before) in beforeHashes)
    {
        var after = SHA256.HashData(File.ReadAllBytes(path));
        AssertTrue(before.SequenceEqual(after),
            $"File '{path}' must not be modified by a proposal-only flow.");
    }

    // Provenance is captured.
    var prov = store.ReadAllAsync().GetAwaiter().GetResult();
    AssertEqual(1, prov.Count);
    var pp = prov[0] as PatchProposalProvenance;
    AssertTrue(pp is not null, "Expected PatchProposalProvenance in store.");
    AssertEqual("test-session-pr23", pp!.SessionId);
    AssertFalse(pp.WasApplied, "WasApplied must be false in a proposal-only flow.");
    AssertEqual("High", pp.RiskLevel);
}

static void AiPatchProposalProvenanceRoundTrips()
{
    var prov = new PatchProposalProvenance(
        Timestamp: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ProposalHash: "abcdef1234567890",
        ChangedFileCount: 3,
        RiskLevel: "High",
        WasApplied: false,
        SessionId: "s1");

    var json = ProvenanceSerializer.Serialize(prov);
    var roundTripped = ProvenanceSerializer.Deserialize(json);

    AssertTrue(roundTripped is PatchProposalProvenance pp && pp.ProposalHash == "abcdef1234567890",
        $"Expected PatchProposalProvenance to round-trip via ProvenanceSerializer. JSON: {json}");
    AssertTrue(json.Contains("\"kind\""), $"Serialised provenance must include kind discriminator: {json}");
    AssertTrue(json.Contains("PatchProposal"), $"Kind must be 'PatchProposal': {json}");
    AssertFalse(json.Contains("unifiedDiff", StringComparison.OrdinalIgnoreCase),
        "Provenance JSON must not include raw diff content.");
}

// ─── PR-18: Ollama Provider tests ────────────────────────────────────────────

static void OllamaRuntimeReturnsSuccessFromFakeHandler()
{
    const string responseJson = """{"response": "{\"answer\": 42}", "done": true}""";
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new FakeHttpHandler(
            url => url.Contains("/api/generate"),
            _ => (System.Net.HttpStatusCode.OK, responseJson)));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(result.Success, $"Expected success. Error: {result.ErrorMessage}");
    AssertEqual("{\"answer\": 42}", result.OutputPayload);
    AssertFalse(string.IsNullOrWhiteSpace(result.OutputHash),
        "Expected non-empty output hash.");
    AssertTrue(result.OutputHash!.StartsWith("sha256:"),
        $"Hash should start with sha256:. Got: {result.OutputHash}");
}

static void OllamaRuntimeReturnsFailureOnHttpError()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new FakeHttpHandler(
            _ => true,
            _ => (System.Net.HttpStatusCode.InternalServerError, "model error")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertFalse(result.Success, "Expected failure on HTTP 500.");
    AssertTrue(result.ErrorMessage?.Contains("500") == true,
        $"Expected error to mention HTTP 500. Got: {result.ErrorMessage}");
}

static void OllamaRuntimeReturnsFailureOnNetworkError()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new ThrowingHttpHandler(new HttpRequestException("Connection refused")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertFalse(result.Success, "Expected failure when server is unreachable.");
    AssertTrue(result.ErrorMessage?.Contains("Ollama request failed") == true,
        $"Expected 'Ollama request failed' in error. Got: {result.ErrorMessage}");
}

static void OllamaRuntimeHealthCheckPassesWithFakeServer()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new FakeHttpHandler(
            _ => true,
            _ => (System.Net.HttpStatusCode.OK, "Ollama is running")));

    var healthy = runtime.HealthCheckAsync().GetAwaiter().GetResult();
    AssertTrue(healthy, "Health check should pass with a reachable server.");
}

static void OllamaRuntimeHealthCheckFailsWhenUnreachable()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new ThrowingHttpHandler(new HttpRequestException("Connection refused")));

    var healthy = runtime.HealthCheckAsync().GetAwaiter().GetResult();
    AssertFalse(healthy, "Health check should fail when server is unreachable.");
}

static void OllamaRuntimeRejectsMissingSchemaName()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new FakeHttpHandler(_ => true, _ => (System.Net.HttpStatusCode.OK, "{}")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: null,
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    try
    {
        runtime.ExecuteAsync(request).GetAwaiter().GetResult();
        throw new Exception("Expected ArgumentException for missing schema name.");
    }
    catch (ArgumentException ex)
    {
        AssertTrue(ex.Message.Contains("missing required output schema"),
            $"Expected 'missing required output schema' in exception. Got: {ex.Message}");
    }
}

static void OllamaRuntimeAppliesReplayOptions()
{
    string? capturedBody = null;

    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3" },
        httpHandler: new CapturingHttpHandler(
            body => capturedBody = body,
            _ => (System.Net.HttpStatusCode.OK, """{"response": "ok", "done": true}""")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: null,
        Replay: new ModelReplayOptions(Seed: 42, Temperature: 0.0));

    runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(capturedBody is not null, "Expected request body to be captured.");
    AssertTrue(capturedBody!.Contains("\"seed\"") || capturedBody.Contains("seed"),
        $"Expected 'seed' in request body. Got: {capturedBody}");
    AssertTrue(capturedBody.Contains("42"),
        $"Expected seed value 42 in request body. Got: {capturedBody}");
}

static void OllamaRuntimeSendsExpectedGenerateRequestShape()
{
    HttpRequestMessage? capturedRequest = null;
    string? capturedBody = null;

    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig
        {
            Endpoint = new Uri("http://127.0.0.1:11435"),
            Model = "qwen2.5-coder",
            RequestJsonFormat = true
        },
        httpHandler: new CapturingHttpHandler(
            body => capturedBody = body,
            request => capturedRequest = request,
            _ => (System.Net.HttpStatusCode.OK, """{"response": "{\"ok\": true}", "done": true}""")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "qwen2.5-coder", Task: "summarize",
        InputPayload: "{\"text\":\"hello\"}", SchemaName: "Summary",
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(result.Success, $"Expected success. Error: {result.ErrorMessage}");
    AssertTrue(capturedRequest is not null, "Expected HTTP request to be captured.");
    AssertEqual(HttpMethod.Post, capturedRequest!.Method);
    AssertEqual("http://127.0.0.1:11435/api/generate", capturedRequest.RequestUri!.ToString());

    using var doc = JsonDocument.Parse(capturedBody ?? throw new Exception("Expected captured request body."));
    var root = doc.RootElement;
    AssertEqual("qwen2.5-coder", root.GetProperty("model").GetString());
    AssertEqual(false, root.GetProperty("stream").GetBoolean());
    AssertEqual("json", root.GetProperty("format").GetString());
    AssertTrue(root.GetProperty("prompt").GetString()!.Contains("Task: summarize"),
        $"Expected task in prompt. Body: {capturedBody}");
    AssertTrue(root.GetProperty("prompt").GetString()!.Contains("{\"text\":\"hello\"}"),
        $"Expected payload in prompt. Body: {capturedBody}");
}

static void OllamaRuntimeAcceptsLocalOnlyPolicyConfiguration()
{
    var policy = new PolicyRuntimeContext([
        new PolicyDecision(
            "SourcePolicy",
            ClassificationLevel.Internal,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    ]);

    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig { Model = "llama3", PolicyName = "SourcePolicy" },
        policy,
        new FakeHttpHandler(
            url => url.Contains("/api/generate"),
            _ => (System.Net.HttpStatusCode.OK, """{"response": "{\"answer\": 42}", "done": true}""")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: "SourcePolicy");

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(result.Success,
        $"Local Ollama should be allowed under a local-only policy. Error: {result.ErrorMessage}");
}

static void OllamaRuntimeReportsActionableUnavailableDiagnostics()
{
    using var runtime = new OllamaModelRuntime(
        new OllamaProviderConfig
        {
            Endpoint = new Uri("http://localhost:11434"),
            Model = "llama3"
        },
        httpHandler: new ThrowingHttpHandler(new HttpRequestException("Connection refused")));

    var request = new ModelRequest(
        Provider: "ollama", Model: "llama3", Task: "answer",
        InputPayload: "{}", SchemaName: "TestSchema",
        ProviderKind: ModelProviderKind.Local, PolicyName: null);

    var result = runtime.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertFalse(result.Success, "Expected failure when Ollama is unavailable.");
    AssertTrue(result.ErrorMessage?.Contains("http://localhost:11434", StringComparison.Ordinal) == true,
        $"Expected endpoint in error. Got: {result.ErrorMessage}");
    AssertTrue(result.ErrorMessage?.Contains("llama3", StringComparison.Ordinal) == true,
        $"Expected model in error. Got: {result.ErrorMessage}");
    AssertTrue(result.ErrorMessage?.Contains("ollama serve", StringComparison.OrdinalIgnoreCase) == true,
        $"Expected recovery hint in error. Got: {result.ErrorMessage}");
}

// ─── PR-25: AISharp.LanguageServer tests ─────────────────────────────────────

static void LspServiceOpensDocumentAndReturnsDiagnostics()
{
    var service = new LanguageServerService();
    const string invalidSource = "fn foo() { unknown_fn(); }";

    var result = service.OpenDocument("file:///test.aisharp", invalidSource, version: 1);

    AssertEqual("file:///test.aisharp", result.Uri);
    AssertEqual(1, result.Version);
    AssertTrue(result.Diagnostics is not null, "Expected non-null diagnostics list.");
}

static void LspServiceReturnsNoDiagnosticsForValidDocument()
{
    var service = new LanguageServerService();
    const string validSource = """
        intent "Manage customer orders"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var result = service.OpenDocument("file:///valid.aisharp", validSource, version: 1);

    AssertTrue(result.Diagnostics.Count == 0,
        $"Expected no diagnostics for valid source. Got: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
}

static void LspServiceChangeDocumentUpdatesSnapshot()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///test.aisharp", "fn bad() { }", version: 1);

    const string validSource = """
        intent "Manage customer orders"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var result = service.ChangeDocument("file:///test.aisharp", validSource, newVersion: 2);

    AssertEqual(2, result.Version);
    AssertEqual("file:///test.aisharp", result.Uri);

    var snapshot = service.Store.TryGet("file:///test.aisharp");
    AssertTrue(snapshot is not null, "Snapshot should exist after change.");
    AssertEqual(validSource, snapshot!.Text);
    AssertEqual(2, snapshot.Version);
}

static void LspServiceCloseDocumentRemovesFromStore()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///test.aisharp", "fn bad() { }", version: 1);

    AssertEqual(1, service.Store.Count);

    var closed = service.CloseDocument("file:///test.aisharp");

    AssertTrue(closed, "CloseDocument should return true for open document.");
    AssertEqual(0, service.Store.Count);
    AssertTrue(service.Store.TryGet("file:///test.aisharp") is null,
        "Document should not be accessible after close.");
}

static void LspServiceGetDiagnosticsThrowsForUnopenedDocument()
{
    var service = new LanguageServerService();
    try
    {
        service.GetDiagnostics("file:///never-opened.aisharp");
        throw new Exception("Expected InvalidOperationException for unopened document.");
    }
    catch (InvalidOperationException ex)
    {
        AssertTrue(ex.Message.Contains("not open"),
            $"Expected 'not open' in exception message. Got: {ex.Message}");
    }
}

static void LspServiceMaintainsVersionThroughChanges()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///versioned.aisharp", "fn a() { }", version: 10);

    var result = service.ChangeDocument("file:///versioned.aisharp", "fn b() { }");

    // Version should auto-increment from 10 to 11
    AssertEqual(11, result.Version);
}

static void LspDocumentStoreTracksMultipleDocuments()
{
    var store = new DocumentStore();

    store.Open("file:///a.aisharp", "fn a() { }", version: 1);
    store.Open("file:///b.aisharp", "fn b() { }", version: 1);
    store.Open("file:///c.aisharp", "fn c() { }", version: 1);

    AssertEqual(3, store.Count);

    var uris = store.OpenUris();
    AssertEqual(3, uris.Count);
    AssertTrue(uris.Contains("file:///a.aisharp"), "Expected a.aisharp in open URIs.");
    AssertTrue(uris.Contains("file:///b.aisharp"), "Expected b.aisharp in open URIs.");
    AssertTrue(uris.Contains("file:///c.aisharp"), "Expected c.aisharp in open URIs.");

    store.Close("file:///b.aisharp");
    AssertEqual(2, store.Count);
    AssertTrue(store.TryGet("file:///b.aisharp") is null,
        "b.aisharp should be removed after close.");
}

static void LspChangingInvalidToValidClearsDiagnostics()
{
    var service = new LanguageServerService();
    const string invalidSource = """
        intent "Orders"
        policy CustomerPII {
          classification = invalid
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;
    const string validSource = """
        intent "Orders"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var opened = service.OpenDocument("file:///diag-clear.aisharp", invalidSource, version: 1);
    AssertTrue(opened.Diagnostics.Count > 0, "Expected diagnostics for invalid source.");

    var changed = service.ChangeDocument("file:///diag-clear.aisharp", validSource, newVersion: 2);

    AssertEqual(0, changed.Diagnostics.Count, "Expected diagnostics to clear after fixing the document.");
}

static void LspChangingValidToInvalidPublishesDiagnostics()
{
    var service = new LanguageServerService();
    const string validSource = """
        intent "Orders"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;
    const string invalidSource = """
        intent "Orders"
        policy CustomerPII {
          classification = invalid
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var opened = service.OpenDocument("file:///diag-add.aisharp", validSource, version: 1);
    AssertEqual(0, opened.Diagnostics.Count, "Expected valid source to open cleanly.");

    var changed = service.ChangeDocument("file:///diag-add.aisharp", invalidSource, newVersion: 2);

    AssertTrue(changed.Diagnostics.Count > 0, "Expected diagnostics after introducing an invalid policy value.");
}

static void LspDiagnosticLocationsAreZeroBased()
{
    const string invalidSource = """
        intent "Orders"
        policy CustomerPII {
          classification = invalid
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var result = AISharpLanguageServer.GetDiagnostics(
        new LanguageServerTextDocument("file:///zero-based.aisharp", invalidSource, Version: 1));

    var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == "AI2023");
    AssertTrue(diagnostic is not null, "Expected invalid classification diagnostic AI2023.");
    AssertEqual(1, diagnostic!.Range.Start.Line, "Expected 0-based line index for the policy declaration.");
}

static void LspIncrementalChangeAppliesPartialTextReplacement()
{
    var updated = LanguageServerService.ApplyTextChanges(
        "hello world",
        [new TextChangeRange(0, 6, 0, 11, "AI#")]);

    AssertEqual("hello AI#", updated);
}

static void LspIncrementalChangeOnServiceUpdatesSnapshotAndReturnsNewDiagnostics()
{
    var service = new LanguageServerService();
    const string invalidSource = """
        intent "Orders"
        policy CustomerPII {
        classification = invalid
        inference = local_only
        training = forbidden
        telemetry = opt_in_aggregate
        }
        """;

    var opened = service.OpenDocument("file:///incremental.aisharp", invalidSource, version: 3);
    AssertTrue(opened.Diagnostics.Count > 0, "Expected diagnostics before incremental fix.");

    var changed = service.ChangeDocumentIncremental(
        "file:///incremental.aisharp",
        [new TextChangeRange(2, 17, 2, 24, "internal")],
        newVersion: 4);

    AssertEqual(0, changed.Diagnostics.Count, "Expected incremental edit to clear diagnostics.");
    var snapshot = service.Store.TryGet("file:///incremental.aisharp");
    AssertTrue(snapshot is not null, "Expected updated snapshot after incremental change.");
    AssertTrue(snapshot!.Text.Contains("classification = internal", StringComparison.Ordinal), "Snapshot text should include the incremental edit.");
    AssertEqual(4, snapshot.Version);
}

static void LspGetDocumentSymbolsReturnsTopLevelDeclarations()
{
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "orders.aisharp");
    var symbols = EditorIntelligence.GetDocumentSymbols(File.ReadAllText(samplePath));

    AssertTrue(symbols.Any(symbol => symbol.Kind == "policy" && symbol.Name == "CustomerPII"), "Expected CustomerPII policy symbol.");
    AssertTrue(symbols.Any(symbol => symbol.Kind == "function" && symbol.Name == "getActiveOrders"), "Expected getActiveOrders function symbol.");
    AssertTrue(symbols.Any(symbol => symbol.Kind == "schema" && symbol.Name == "OrderPatch"), "Expected OrderPatch schema symbol.");
}

static void LspGetDocumentSymbolsIncludesKind()
{
    const string source = """
        intent "Orders"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var symbol = EditorIntelligence.GetDocumentSymbols(source).Single();

    AssertEqual("policy", symbol.Kind);
}

static void LspGetHoverReturnsSymbolInfo()
{
    const string source = """
        intent "Hover"
        fn greet() -> int
            requires true
        {
            return 1
        }
        """;

    var hover = EditorIntelligence.GetHover(source, 1, 3);

    AssertTrue(hover is not null, "Expected hover info at the function name.");
    AssertTrue(hover!.Contents.Contains("greet", StringComparison.Ordinal), "Hover should mention the symbol name.");
    AssertTrue(hover.Contents.Contains("function", StringComparison.Ordinal), "Hover should mention the symbol kind.");
}

static void LspGetCompletionsIncludesKeywords()
{
    var completions = EditorIntelligence.GetCompletions("intent \"Complete\"\n", 0, 0);

    AssertTrue(completions.Any(item => item.Label == "fn"), "Expected fn keyword completion.");
    AssertTrue(completions.Any(item => item.Label == "policy"), "Expected policy keyword completion.");
    AssertTrue(completions.Any(item => item.Label == "schema"), "Expected schema keyword completion.");
}

static void LspGetCompletionsIncludesDocumentSymbols()
{
    const string source = """
        intent "Complete"
        policy CustomerPII {
          classification = internal
          inference = local_only
          training = forbidden
          telemetry = opt_in_aggregate
        }
        """;

    var completions = EditorIntelligence.GetCompletions(source, 0, 0);

    AssertTrue(completions.Any(item => item.Label == "CustomerPII"), "Expected document symbol completion for CustomerPII.");
}

static void LspGetDocumentSymbolsViaServiceForOpenDocument()
{
    var service = new LanguageServerService();
    const string source = """
        intent "Orders"
        schema OrderPatch = {
          customerId: CustomerId,
          status: OrderStatus
        }
        fn update() -> int
            requires true
        {
            return 1
        }
        """;

    service.OpenDocument("file:///symbols.aisharp", source, version: 1);
    var symbols = service.GetDocumentSymbolsForUri("file:///symbols.aisharp");

    AssertTrue(symbols.Any(symbol => symbol.Name == "OrderPatch"), "Expected service to return schema symbol.");
    AssertTrue(symbols.Any(symbol => symbol.Name == "update"), "Expected service to return function symbol.");
}

// ─── PR-27: Enriched symbols, hover, and completion ──────────────────────────

static void LspGetDocumentSymbolsIncludesModuleAndActorHandlers()
{
    const string source = """
        module orders.core
        intent "Orders"
        actor OrderActor {
          state count: int
          fn handle(message: int) -> int
              requires true
          {
              return 1
          }
        }
        """;

    var symbols = EditorIntelligence.GetDocumentSymbols(source);

    AssertTrue(symbols.Any(s => s.Kind == "module" && s.Name == "orders.core"),
        "Expected module symbol from module declaration.");
    AssertTrue(symbols.Any(s => s.Kind == "actor" && s.Name == "OrderActor"),
        "Expected actor symbol.");
    AssertTrue(symbols.Any(s => s.Kind == "actor_handler" && s.Name == "OrderActor.handle"),
        "Expected actor handler symbol named OrderActor.handle.");
}

static void LspGetHoverForFunctionShowsSignatureEffectsAndContracts()
{
    const string source = """
        intent "Hover"
        fn fetchOrders(customerId: int) -> int
            effects [network]
            requires customerId > 0
            ensures result >= 0
        {
            return 1
        }
        """;

    var hover = EditorIntelligence.GetHover(source, 1, 3);

    AssertTrue(hover is not null, "Expected hover info at the function name.");
    AssertTrue(hover!.Contents.Contains("fetchOrders(customerId: int) -> int", StringComparison.Ordinal),
        $"Hover should include function signature, got: {hover.Contents}");
    AssertTrue(hover.Contents.Contains("network", StringComparison.Ordinal),
        $"Hover should mention the network effect, got: {hover.Contents}");
    AssertTrue(hover.Contents.Contains("requires", StringComparison.Ordinal)
        && hover.Contents.Contains("ensures", StringComparison.Ordinal),
        $"Hover should summarise contracts (requires/ensures), got: {hover.Contents}");
}

static void LspGetCompletionsIncludesEffectsAndContractKeywords()
{
    var completions = EditorIntelligence.GetCompletions("intent \"Effects\"\n", 0, 0);

    AssertTrue(completions.Any(item => item.Label == "network" && item.Kind == "effect"),
        "Expected built-in 'network' effect completion.");
    AssertTrue(completions.Any(item => item.Label == "model" && item.Kind == "effect"),
        "Expected built-in 'model' effect completion.");
    AssertTrue(completions.Any(item => item.Label == "requires"),
        "Expected 'requires' contract keyword completion.");
    AssertTrue(completions.Any(item => item.Label == "ensures"),
        "Expected 'ensures' contract keyword completion.");
    AssertTrue(completions.Any(item => item.Label == "invariant"),
        "Expected 'invariant' contract keyword completion.");
}

static void LspGetCompletionsInsideFunctionBodyIncludesParameters()
{
    const string source = """
        intent "Params"
        fn process(customerId: int, total: int) -> int
            requires customerId > 0
        {
            return customerId
        }
        """;

    // The function body line `return customerId` is line index 4 (0-based).
    var completions = EditorIntelligence.GetCompletions(source, 4, 4);

    AssertTrue(completions.Any(item => item.Label == "customerId" && item.Kind == "parameter"),
        "Expected in-scope parameter 'customerId' to appear as a completion inside the function body.");
    AssertTrue(completions.Any(item => item.Label == "total" && item.Kind == "parameter"),
        "Expected in-scope parameter 'total' to appear as a completion inside the function body.");
    AssertTrue(completions.Any(item => item.Label == "return"),
        "Expected the 'return' keyword completion to be available inside a function body.");
}

// ─── PR-19: Optional Remote Model Provider Boundary ──────────────────────────

static void RemoteProviderBoundaryRejectedWithoutPolicyName()
{
    var provider = new FakeRemoteModelProvider();
    var request = new ModelRequest("test", "model", "task", "{}", "Schema",
        ModelProviderKind.Remote, PolicyName: null);
    try
    {
        provider.ExecuteAsync(request).GetAwaiter().GetResult();
        AssertTrue(false, "Expected PolicyViolationException for null policy name.");
    }
    catch (PolicyViolationException ex)
    {
        AssertTrue(ex.Message.Contains("policy name"), "Exception should mention policy name.");
    }
}

static void RemoteProviderBoundaryRejectedWithLocalOnlyPolicy()
{
    var policy = new PolicyRuntimeContext([
        new PolicyDecision("P", ClassificationLevel.Secret,
            InferencePolicy.LocalOnly, TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden, ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    ]);
    var provider = new FakeRemoteModelProvider(policy);
    var request = new ModelRequest("test", "model", "task", "{}", "Schema",
        ModelProviderKind.Remote, PolicyName: "P");
    try
    {
        provider.ExecuteAsync(request).GetAwaiter().GetResult();
        AssertTrue(false, "Expected PolicyViolationException for local-only policy.");
    }
    catch (PolicyViolationException ex)
    {
        AssertTrue(ex.PolicyName == "P", "Policy name should be preserved in exception.");
        AssertTrue(ex.Message.Contains("remote_opt_in"), "Exception should mention remote_opt_in.");
    }
}

static void RemoteProviderBoundaryRejectedWithoutRemoteOptIn()
{
    // Unlisted policy defaults to LocalOnly → rejected.
    var provider = new FakeRemoteModelProvider(new PolicyRuntimeContext());
    var request = new ModelRequest("test", "model", "task", "{}", "Schema",
        ModelProviderKind.Remote, PolicyName: "UnknownPolicy");
    try
    {
        provider.ExecuteAsync(request).GetAwaiter().GetResult();
        AssertTrue(false, "Expected PolicyViolationException for unlisted policy.");
    }
    catch (PolicyViolationException)
    {
        // Expected — default policy is local-only.
    }
}

static void RemoteProviderBoundarySucceedsWithFullOptInPolicy()
{
    var policy = new PolicyRuntimeContext([
        new PolicyDecision("RemotePolicy", ClassificationLevel.Internal,
            InferencePolicy.RemoteOptIn, TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden, ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    ]);
    var provider = new FakeRemoteModelProvider(policy);
    var provenance = new ModelCallProvenance(
        DateTimeOffset.UtcNow, "test", "model", "task",
        "sha256:inputhash", "sha256:outputhash", "Schema", false, null, null);
    provider.Register("test", "model", "task", _ =>
        new ModelResponse(true, """{"ok":true}""", null, "sha256:outputhash", provenance));

    var request = new ModelRequest("test", "model", "task", """{"input":"data"}""",
        "Schema", ModelProviderKind.Remote, PolicyName: "RemotePolicy");
    var response = provider.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(response.Success, "Remote call with full opt-in policy should succeed.");
    AssertTrue(response.Provenance is not null, "Successful remote call must include provenance.");
    AssertTrue(response.Provenance!.Provider == "test", "Provenance should capture provider.");
}

static void RemoteProviderBoundaryProvenanceIncludesProviderKind()
{
    var policy = new PolicyRuntimeContext([
        new PolicyDecision("RemotePolicy", ClassificationLevel.Internal,
            InferencePolicy.RemoteOptIn, TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden, ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    ]);
    var provider = new FakeRemoteModelProvider(policy);
    var provenance = new ModelCallProvenance(
        DateTimeOffset.UtcNow, "remote-provider", "gpt4", "classify",
        null, "sha256:outhash", "Schema", false, null, null);
    provider.Register("remote-provider", "gpt4", "classify", _ =>
        new ModelResponse(true, """{"label":"ok"}""", null, "sha256:outhash", provenance));

    var request = new ModelRequest("remote-provider", "gpt4", "classify",
        """{"text":"classify this"}""", "Schema",
        ModelProviderKind.Remote, PolicyName: "RemotePolicy");
    var response = provider.ExecuteAsync(request).GetAwaiter().GetResult();

    AssertTrue(response.Success, "Successful remote call should be returned.");
    AssertTrue(response.Provenance is not null, "Provenance must be non-null.");
    AssertTrue(response.Provenance!.ProviderKind == ModelProviderKind.Remote,
        "Provenance must explicitly capture the remote provider kind.");
    // Raw payload must not appear in InputHash.
    AssertTrue(response.Provenance!.InputHash is null || !response.Provenance.InputHash.Contains("classify"),
        "Provenance must not store raw input text.");
}

static void RemoteProviderBoundaryHealthCheckReturnsTrue()
{
    var provider = new FakeRemoteModelProvider();
    AssertTrue(provider.ProviderKind == ModelProviderKind.Remote,
        "Remote provider boundary must advertise Remote provider kind.");
    var healthy = provider.HealthCheckAsync().GetAwaiter().GetResult();
    AssertTrue(healthy, "FakeRemoteModelProvider health check should return true.");
}

// ─── PR-24: Policy Engine ─────────────────────────────────────────────────────

static void PolicyEngineDefaultIsLocalOnlyDenyByDefault()
{
    var engine = new PolicyEngine();
    var decision = engine.GetPolicy("anything");
    AssertTrue(decision.Inference == InferencePolicy.LocalOnly,
        "Default policy must be LocalOnly.");
    AssertTrue(decision.Training == TrainingPolicy.Forbidden,
        "Default policy must forbid training.");
    AssertTrue(decision.Telemetry == TelemetryPolicy.Forbidden,
        "Default policy must forbid telemetry.");
    AssertTrue(decision.UnsafeInterop == UnsafeInteropPolicy.Forbidden,
        "Default policy must forbid unsafe interop.");
    AssertTrue(decision.ToolCapability == ToolCapabilityPolicy.DeniedUnlessGranted,
        "Default policy must require explicit tool grants.");
    AssertTrue(decision.ProvenanceRequirement == ProvenanceRequirement.Required,
        "Default policy must require provenance.");
}

static void PolicyEngineRemoteOptInRequiredForRemoteCalls()
{
    var engine = new PolicyEngine(); // default = LocalOnly
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "DefaultPolicy", ModelProviderKind.Remote));
    AssertTrue(!result.Allowed, "Remote call without opt-in must be denied.");
    AssertTrue(result.Errors.Any(d => d.Code == "POLICY_REMOTE_OPT_IN_REQUIRED"),
        "Denial must include POLICY_REMOTE_OPT_IN_REQUIRED diagnostic.");
}

static void PolicyEngineHonorsRuntimeContextDecisions()
{
    var runtimeContext = new PolicyRuntimeContext([
        new PolicyDecision(
            "RemotePolicy",
            ClassificationLevel.Internal,
            InferencePolicy.RemoteOptIn,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated)
    ]);
    var engine = new PolicyEngine(runtimeContext);

    var decision = engine.GetPolicy("RemotePolicy");
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "RemotePolicy",
        ModelProviderKind.Remote,
        HasProvenance: true));

    AssertTrue(decision.Inference == InferencePolicy.RemoteOptIn,
        "PolicyEngine must resolve decisions supplied by PolicyRuntimeContext.");
    AssertTrue(result.Allowed,
        "Remote opt-in from PolicyRuntimeContext must allow remote calls when provenance is present.");
}

static void PolicyEngineLocalCallIsAllowedByDefault()
{
    var engine = new PolicyEngine();
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "LocalPolicy", ModelProviderKind.Local));
    AssertTrue(result.Allowed, "Local call under default policy must be allowed.");
    AssertTrue(!result.Errors.Any(), "No error diagnostics for a basic local call.");
}

static void PolicyEngineToolCapabilitiesDeniedUnlessGranted()
{
    var engine = new PolicyEngine();
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Local,
        HasExplicitToolGrant: false,
        RequestedCapabilities: ["file_read", "http_fetch"]));
    AssertTrue(!result.Allowed, "Tool capabilities must be denied without explicit grant.");
    AssertTrue(result.Errors.Any(d => d.Code == "POLICY_TOOL_CAPABILITY_NOT_GRANTED"),
        "Denial must include POLICY_TOOL_CAPABILITY_NOT_GRANTED diagnostic.");
}

static void PolicyEngineToolCapabilitiesAllowedWithExplicitGrant()
{
    var engine = new PolicyEngine([
        new PolicyEngineDecision("P",
            ClassificationLevel.Internal,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated,
            ToolCapabilityPolicy.DeniedUnlessGranted,
            UnsafeInteropPolicy.Forbidden,
            ProvenanceRequirement.Optional)
    ]);
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Local,
        HasExplicitToolGrant: true,
        RequestedCapabilities: ["file_read"]));
    AssertTrue(result.Allowed, "Tool capabilities allowed when explicit grant is present.");
    AssertTrue(!result.Errors.Any(d => d.Code == "POLICY_TOOL_CAPABILITY_NOT_GRANTED"),
        "No tool capability error when grant is present.");
}

static void PolicyEngineUnsafeInteropForbiddenByDefault()
{
    var engine = new PolicyEngine();
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Local, RequestsUnsafeInterop: true));
    AssertTrue(!result.Allowed, "Unsafe interop must be denied by default.");
    AssertTrue(result.Errors.Any(d => d.Code == "POLICY_UNSAFE_INTEROP_FORBIDDEN"),
        "Denial must include POLICY_UNSAFE_INTEROP_FORBIDDEN diagnostic.");
}

static void PolicyEngineUnsafeInteropAllowedWhenDeclared()
{
    var engine = new PolicyEngine([
        new PolicyEngineDecision("P",
            ClassificationLevel.Internal,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated,
            ToolCapabilityPolicy.DeniedUnlessGranted,
            UnsafeInteropPolicy.Allowed,
            ProvenanceRequirement.Optional)
    ]);
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Local, RequestsUnsafeInterop: true));
    AssertTrue(result.Allowed, "Unsafe interop allowed when declared in policy.");
    AssertTrue(!result.Errors.Any(d => d.Code == "POLICY_UNSAFE_INTEROP_FORBIDDEN"),
        "No unsafe interop error when policy declares allowed.");
}

static void PolicyEngineProvenanceRequiredForRemoteCalls()
{
    var engine = new PolicyEngine([
        new PolicyEngineDecision("RemotePolicy",
            ClassificationLevel.Internal,
            InferencePolicy.RemoteOptIn,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.Forbidden,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated,
            ToolCapabilityPolicy.DeniedUnlessGranted,
            UnsafeInteropPolicy.Forbidden,
            ProvenanceRequirement.Required)
    ]);
    // Remote call without provenance
    var denied = engine.Evaluate(new PolicyEvaluationRequest(
        "RemotePolicy", ModelProviderKind.Remote, HasProvenance: false));
    AssertTrue(!denied.Allowed, "Remote call without provenance must be denied.");
    AssertTrue(denied.Errors.Any(d => d.Code == "POLICY_PROVENANCE_REQUIRED"),
        "Denial must include POLICY_PROVENANCE_REQUIRED diagnostic.");

    // Remote call with provenance
    var allowed = engine.Evaluate(new PolicyEvaluationRequest(
        "RemotePolicy", ModelProviderKind.Remote, HasProvenance: true));
    AssertTrue(allowed.Allowed, "Remote call with provenance should be allowed.");
}

static void PolicyEngineDiagnosticsRecordedForEveryDenial()
{
    var engine = new PolicyEngine(); // default = LocalOnly, all restricted
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Remote,
        HasExplicitToolGrant: false,
        RequestsUnsafeInterop: true,
        HasProvenance: false,
        RequestedCapabilities: ["http_fetch"]));

    // Should get: remote opt-in, tool capability, unsafe interop, provenance denials
    AssertTrue(!result.Allowed, "Multiple violations must result in denied.");
    AssertTrue(result.Diagnostics.Count >= 3,
        $"Expected at least 3 diagnostics for multiple violations, got {result.Diagnostics.Count}.");
    AssertTrue(result.Errors.All(d => d.PolicyName == "P"),
        "All diagnostics must reference the evaluated policy name.");
}

static void PolicyEngineThrowIfDeniedConvertsToException()
{
    var engine = new PolicyEngine();
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "P", ModelProviderKind.Remote));
    AssertTrue(!result.Allowed, "Expected denial.");
    try
    {
        result.ThrowIfDenied();
        AssertTrue(false, "Expected PolicyViolationException.");
    }
    catch (PolicyViolationException ex)
    {
        AssertTrue(ex.PolicyName == "P", "Exception must reference policy name 'P'.");
    }
}

static void PolicyEngineTelemetryEnabledEmitsWarning()
{
    var engine = new PolicyEngine([
        new PolicyEngineDecision("TelemetryPolicy",
            ClassificationLevel.Public,
            InferencePolicy.LocalOnly,
            TrainingPolicy.Forbidden,
            TelemetryPolicy.OptInAggregate,
            ExternalTextPolicy.Tainted,
            CapabilitiesPolicy.Separated,
            ToolCapabilityPolicy.DeniedUnlessGranted,
            UnsafeInteropPolicy.Forbidden,
            ProvenanceRequirement.Optional)
    ]);
    var result = engine.Evaluate(new PolicyEvaluationRequest(
        "TelemetryPolicy", ModelProviderKind.Local));
    AssertTrue(result.Allowed, "Telemetry enabled should not block operation.");
    AssertTrue(result.Warnings.Any(d => d.Code == "POLICY_TELEMETRY_ENABLED"),
        "Telemetry enabled must emit POLICY_TELEMETRY_ENABLED warning.");
}

// ─── PR-28: LSP Go-To-Definition and References ───────────────────────────────

static string LspNavDocText() => """
    module nav.test
    schema OrderPatch = {
        id: String,
        status: String
    }
    fn applyPatch(p: OrderPatch): String {
        "applied"
    }
    fn callApply(): String {
        applyPatch({ id: "1", status: "ok" })
    }
    """;

static void LspGoToDefinitionResolvesSchemaDeclaration()
{
    var result = EditorIntelligence.GetDefinition(LspNavDocText(), 1, 10);
    AssertTrue(result is not null, "GetDefinition should resolve OrderPatch.");
    AssertTrue(result!.Range.Start.Line == 1, $"Declaration should be on line 1, got {result.Range.Start.Line}.");
}

static void LspGoToDefinitionResolvesFunctionDeclaration()
{
    var result = EditorIntelligence.GetDefinition(LspNavDocText(), 9, 5);
    AssertTrue(result is not null, "GetDefinition should resolve applyPatch function call.");
    AssertTrue(result!.Range.Start.Line == 5, $"Function declaration should be on line 5, got {result.Range.Start.Line}.");
}

static void LspGoToDefinitionReturnsNullForUnknownWord()
{
    // line 0 is "    module nav.test" — "module" is a keyword, not a declared symbol
    var result = EditorIntelligence.GetDefinition(LspNavDocText(), 0, 6);
    AssertTrue(result is null, "GetDefinition should return null for keyword/undeclared token.");
}

static void LspFindReferencesIncludesDeclarationAndUsages()
{
    // "OrderPatch" appears in: schema declaration (line 1), fn signature (line 5)
    var refs = EditorIntelligence.GetReferences(LspNavDocText(), 1, 10);
    AssertTrue(refs.Count >= 2,
        $"Expected at least 2 references for OrderPatch (declaration + usage), got {refs.Count}.");
    AssertTrue(refs.Any(r => r.Range.Start.Line == 1), "References must include the declaration line.");
    AssertTrue(refs.Any(r => r.Range.Start.Line == 5), "References must include usage in fn signature.");
}

static void LspFindReferencesViaServiceReturnsUri()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///nav.aisharp", LspNavDocText());
    var refs = service.GetReferences("file:///nav.aisharp", 1, 10);
    AssertTrue(refs.Count >= 2, $"Expected at least 2 references, got {refs.Count}.");
    AssertTrue(refs.All(r => r.Uri == "file:///nav.aisharp"),
        "All reference results must carry the document URI.");
}

static void LspGoToDefinitionViaServiceResolvesSchema()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///nav.aisharp", LspNavDocText());
    var result = service.GetDefinition("file:///nav.aisharp", 1, 10);
    AssertTrue(result is not null, "Service GetDefinition should resolve OrderPatch.");
    AssertTrue(result!.Uri == "file:///nav.aisharp", "Result must carry document URI.");
    AssertTrue(result.Range.Start.Line == 1, "Result should point to schema declaration line.");
}

static void LspServiceFormatsOpenDocument()
{
    var service = new LanguageServerService();
    service.OpenDocument("file:///format.aisharp", "intent \"Format\"   \r\nfn ok() -> int\r\nrequires true\r\n{\r\nreturn 1\r\n}");

    var edit = service.FormatDocument("file:///format.aisharp");

    AssertEqual("intent \"Format\"\nfn ok() -> int\n    requires true\n{\n    return 1\n}\n", edit.FormattedText);
    AssertTrue(edit.Changed, "Formatting should report a changed document.");
    AssertEqual(edit.FormattedText, service.Store.TryGet("file:///format.aisharp")!.Text);
}

// ─── PR-30: DAP Debug Boundary ────────────────────────────────────────────────

static string DebugTestSource() => """
    intent "debug.test"
    fn add(a: int, b: int) -> int
    {
        return a
    }
    fn greet(name: string) -> string
    {
        return name
    }
    """;

static void DebugMapLoweringProducesSourceMapArtifact()
{
    var mirResult = AISharpMirBuilder.Build(AISharpHirBuilder.Build(AISharpSemanticModel.Build(AISharpParser.Parse(DebugTestSource()))));
    var lowerResult = AISharpLowering.Lower(mirResult.Document);

    AssertTrue(lowerResult.DebugMap is not null, "Lowering must produce a DebugMap artifact.");
    AssertTrue(lowerResult.DebugMap!.Entries.Count >= 2,
        $"Debug map should have at least 2 entries (one per function), got {lowerResult.DebugMap.Entries.Count}.");
    AssertTrue(lowerResult.DebugMap.Entries.Any(e => e.MemberName == "fn:add"),
        "Debug map must include entry for fn:add.");
    AssertTrue(lowerResult.DebugMap.Entries.Any(e => e.MemberName == "fn:greet"),
        "Debug map must include entry for fn:greet.");
}

static void DebugMapForwardMappingResolvesFunctionBodyLine()
{
    var mirResult = AISharpMirBuilder.Build(AISharpHirBuilder.Build(AISharpSemanticModel.Build(AISharpParser.Parse(DebugTestSource()))));
    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var debugMap = lowerResult.DebugMap!;

    // fn:add is declared at line 1 in the source (0-based)
    var addEntry = debugMap.Entries.First(e => e.MemberName == "fn:add");
    var aiLine = addEntry.AiSharpSpan.StartLine;

    var forward = debugMap.MapForward(aiLine, 0);
    AssertTrue(forward is not null, "Forward map must resolve fn:add source line.");
    AssertTrue(forward!.MemberName == "fn:add",
        $"Forward map must return fn:add entry, got {forward.MemberName}.");
    AssertTrue(forward.GeneratedSpan.StartLine >= 0,
        "Generated span start line must be non-negative.");
}

static void DebugMapReverseMappingResolvesGeneratedLine()
{
    var mirResult = AISharpMirBuilder.Build(AISharpHirBuilder.Build(AISharpSemanticModel.Build(AISharpParser.Parse(DebugTestSource()))));
    var lowerResult = AISharpLowering.Lower(mirResult.Document);
    var debugMap = lowerResult.DebugMap!;

    // Take the first entry and verify we can reverse-map its generated span.
    var entry = debugMap.Entries.First();
    var reverse = debugMap.MapReverse(entry.GeneratedSpan.StartLine, 0);
    AssertTrue(reverse is not null,
        $"Reverse map must resolve generated line {entry.GeneratedSpan.StartLine}.");
    AssertTrue(reverse!.MemberName == entry.MemberName,
        $"Reverse map must return {entry.MemberName}, got {reverse.MemberName}.");
}

static void DebugMapAllEntriesHaveMemberNames()
{
    var mirResult = AISharpMirBuilder.Build(AISharpHirBuilder.Build(AISharpSemanticModel.Build(AISharpParser.Parse(DebugTestSource()))));
    var debugMap = AISharpLowering.Lower(mirResult.Document).DebugMap!;
    AssertTrue(debugMap.Entries.All(e => !string.IsNullOrEmpty(e.MemberName)),
        "Every debug map entry must have a non-empty member name.");
    AssertTrue(debugMap.Entries.All(e => e.MemberName.StartsWith("fn:", StringComparison.Ordinal)),
        "All debug map entries must use 'fn:' prefix.");
}

// ─── Test helper types ────────────────────────────────────────────────────────

// ─── PR-16 Generated Project Build Harness tests ──────────────────────────────

static string LowerAISharpSampleToCSharp(string samplePath)
{
    var source = File.ReadAllText(samplePath);
    var parseResult    = AISharpParser.Parse(source);
    var semanticResult = AISharpSemanticModel.Build(parseResult);
    var hirResult      = AISharpHirBuilder.Build(semanticResult);
    var mirResult      = AISharpMirBuilder.Build(hirResult);
    var lowerResult    = AISharpLowering.Lower(mirResult.Document);
    return lowerResult.GeneratedCSharpSource;
}

static void BuildHarnessCompilesGeneratedCSharpFromHelloSample()
{
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "hello.aisharp");
    AssertTrue(File.Exists(samplePath), $"Build harness sample missing: {samplePath}");

    var generated = LowerAISharpSampleToCSharp(samplePath);
    AssertTrue(!string.IsNullOrWhiteSpace(generated), "Lowered hello sample should produce non-empty C#.");

    var result = GeneratedProjectBuildHarness.Build(generated, testName: "hello_sample");

    AssertTrue(result.Succeeded,
        "Build harness failed to compile generated C# from samples/hello.aisharp.\n" +
        result.FormatDiagnostics() +
        "\n--- Generated C# ---\n" + generated);
    AssertEqual(0, result.ExitCode, "Successful build should report exit code 0.");
    AssertFalse(result.Preserved, "Successful build should not preserve artifacts when env var is unset.");
    AssertFalse(Directory.Exists(result.ProjectDirectory),
        $"Build harness should clean up '{result.ProjectDirectory}' when preservation is not requested.");
}

static void BuildHarnessProducesGeneratedAssemblyArtifactForSample()
{
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "hello.aisharp");
    var generated  = LowerAISharpSampleToCSharp(samplePath);

    // Preserve the artifacts so we can verify the assembly path the harness reports.
    Environment.SetEnvironmentVariable(GeneratedProjectBuildHarness.PreserveArtifactsVariable, "1");
    GeneratedProjectBuildResult result;
    try
    {
        result = GeneratedProjectBuildHarness.Build(generated, testName: "hello_assembly");
    }
    finally
    {
        Environment.SetEnvironmentVariable(GeneratedProjectBuildHarness.PreserveArtifactsVariable, null);
    }

    try
    {
        AssertTrue(result.Succeeded,
            "Build harness should compile generated C# for assembly artifact assertion.\n" +
            result.FormatDiagnostics());
        AssertTrue(result.GeneratedAssemblyPath is not null,
            "Build harness should report the produced assembly path on success. " + result.FormatDiagnostics());
        AssertTrue(File.Exists(result.GeneratedAssemblyPath!),
            $"Reported assembly path should exist on disk: {result.GeneratedAssemblyPath}");
        AssertTrue(result.GeneratedAssemblyPath!.EndsWith("hello_assembly.dll", StringComparison.OrdinalIgnoreCase),
            $"Assembly path should match the requested test name, got: {result.GeneratedAssemblyPath}");
    }
    finally
    {
        // Manual cleanup because we requested preservation.
        if (Directory.Exists(result.ProjectDirectory))
        {
            try { Directory.Delete(result.ProjectDirectory, recursive: true); } catch { }
        }
    }
}

static void BuildHarnessCapturesUsefulDiagnosticsOnBrokenGeneratedCSharp()
{
    // Intentionally invalid C# so the harness exercises its failure-capture path.
    const string broken = "namespace BrokenHarness { public class C { public void M() { this is not valid csharp } } }";

    var result = GeneratedProjectBuildHarness.Build(broken, testName: "broken_sample");

    AssertFalse(result.Succeeded, "Broken C# should not produce a successful build.");
    AssertTrue(result.ExitCode != 0, "Broken C# should report a non-zero exit code.");
    var diagnostics = result.StandardOutput + "\n" + result.StandardError;
    AssertTrue(
        diagnostics.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("error :", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("error", StringComparison.OrdinalIgnoreCase),
        "Captured diagnostics should include compiler error information. Captured:\n" + diagnostics);
    AssertTrue(result.GeneratedAssemblyPath is null,
        "Failed build should not report a generated assembly path.");
    AssertFalse(Directory.Exists(result.ProjectDirectory),
        "Build harness should still clean up its temporary directory on failure.");
}

static void BuildHarnessPreservesArtifactsWhenDebugEnvironmentVariableIsSet()
{
    var samplePath = Path.Combine(FindRepositoryRoot(), "samples", "hello.aisharp");
    var generated  = LowerAISharpSampleToCSharp(samplePath);

    Environment.SetEnvironmentVariable(GeneratedProjectBuildHarness.PreserveArtifactsVariable, "true");
    GeneratedProjectBuildResult result;
    try
    {
        result = GeneratedProjectBuildHarness.Build(generated, testName: "hello_preserve");
    }
    finally
    {
        Environment.SetEnvironmentVariable(GeneratedProjectBuildHarness.PreserveArtifactsVariable, null);
    }

    try
    {
        AssertTrue(result.Preserved, "Result should report Preserved=true when the env var is set.");
        AssertTrue(Directory.Exists(result.ProjectDirectory),
            $"Project directory should be preserved on disk for debugging: {result.ProjectDirectory}");
        AssertTrue(File.Exists(result.ProjectFilePath),
            $"Preserved .csproj should still exist: {result.ProjectFilePath}");
        AssertTrue(File.Exists(result.GeneratedSourcePath),
            $"Preserved Generated.cs should still exist: {result.GeneratedSourcePath}");
    }
    finally
    {
        if (Directory.Exists(result.ProjectDirectory))
        {
            try { Directory.Delete(result.ProjectDirectory, recursive: true); } catch { }
        }
    }
}

static void ServiceLibrarySampleLowersAndBuildsEndToEnd()
{
    // PR-37 acceptance: each .aisharp source in samples/service-library must
    // lower to compilable C# that the project build harness can compile.
    var sampleDir = Path.Combine(FindRepositoryRoot(), "samples", "service-library");
    AssertTrue(Directory.Exists(sampleDir), $"Service-library sample directory missing: {sampleDir}");

    var manifestPath = Path.Combine(sampleDir, "aisharp.toml");
    var readmePath   = Path.Combine(sampleDir, "README.md");
    AssertTrue(File.Exists(manifestPath), $"Sample manifest missing: {manifestPath}");
    AssertTrue(File.Exists(readmePath),   $"Sample README missing: {readmePath}");

    var aisharpFiles = Directory.GetFiles(sampleDir, "*.aisharp").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    AssertTrue(aisharpFiles.Count >= 3, $"Service-library sample should contain multiple AI# files (found {aisharpFiles.Count}).");

    foreach (var samplePath in aisharpFiles)
    {
        var fileName = Path.GetFileNameWithoutExtension(samplePath);

        var source         = File.ReadAllText(samplePath);
        var parseResult    = AISharpParser.Parse(source);
        AssertEqual(0, parseResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            $"Sample '{fileName}' must parse without errors. Diagnostics: " +
            string.Join("; ", parseResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var semanticResult = AISharpSemanticModel.Build(parseResult);
        var hirResult      = AISharpHirBuilder.Build(semanticResult);
        var mirResult      = AISharpMirBuilder.Build(hirResult);
        AssertEqual(0, mirResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            $"Sample '{fileName}' must produce no MIR errors. Diagnostics: " +
            string.Join("; ", mirResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var lowerResult = AISharpLowering.Lower(mirResult.Document);
        AssertTrue(!string.IsNullOrWhiteSpace(lowerResult.GeneratedCSharpSource),
            $"Sample '{fileName}' should lower to non-empty C#.");

        var harnessName = "service_library_" + fileName.Replace('-', '_').Replace('.', '_');
        var result = GeneratedProjectBuildHarness.Build(lowerResult.GeneratedCSharpSource, testName: harnessName);

        AssertTrue(result.Succeeded,
            $"Generated C# for service-library sample '{fileName}' should compile. " +
            result.FormatDiagnostics() + "\n--- Generated C# ---\n" + lowerResult.GeneratedCSharpSource);
        AssertEqual(0, result.ExitCode, $"Service-library sample '{fileName}' build should report exit code 0.");
    }
}

sealed class TestDisposableResource : IOwnedResource
{
    private readonly Action _onDispose;
    private bool _consumed;

    public TestDisposableResource(Action onDispose) => _onDispose = onDispose;

    public bool IsConsumed => _consumed;

    public void Dispose()
    {
        if (!_consumed)
        {
            _consumed = true;
            _onDispose();
        }
    }
}

sealed class TestWasmBoundary : WasmToolSandboxBoundary
{
    private readonly HashSet<string> _caps;

    public TestWasmBoundary(IEnumerable<string> capabilities)
        => _caps = new HashSet<string>(capabilities, StringComparer.Ordinal);

    public override IReadOnlySet<string> AvailableCapabilities => _caps;

    public override Task<ToolResponse> InvokeAsync(ToolRequest request)
        => Task.FromResult(new ToolResponse(true, "{}", null, false, TimeSpan.Zero, null));

    public void EnforceCapabilitiesPublic(string toolName, IEnumerable<string> caps)
        => EnforceCapabilities(toolName, caps);
}

// ─── Fake HTTP handlers for Ollama tests ─────────────────────────────────────

sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<string, bool> _urlMatcher;
    private readonly Func<HttpRequestMessage, (System.Net.HttpStatusCode status, string body)> _respond;

    public FakeHttpHandler(
        Func<string, bool> urlMatcher,
        Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> respond)
    {
        _urlMatcher = urlMatcher;
        _respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        if (!_urlMatcher(url))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                { Content = new StringContent("not found") });

        var (status, body) = _respond(request);
        return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body) });
    }
}

sealed class ThrowingHttpHandler : HttpMessageHandler
{
    private readonly Exception _exception;
    public ThrowingHttpHandler(Exception exception) => _exception = exception;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromException<HttpResponseMessage>(_exception);
}

sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly Action<string> _onBody;
    private readonly Action<HttpRequestMessage>? _onRequest;
    private readonly Func<HttpRequestMessage, (System.Net.HttpStatusCode status, string body)> _respond;

    public CapturingHttpHandler(
        Action<string> onBody,
        Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> respond)
        : this(onBody, null, respond)
    {
    }

    public CapturingHttpHandler(
        Action<string> onBody,
        Action<HttpRequestMessage>? onRequest,
        Func<HttpRequestMessage, (System.Net.HttpStatusCode, string)> respond)
    {
        _onBody = onBody;
        _onRequest = onRequest;
        _respond = respond;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _onRequest?.Invoke(request);

        if (request.Content is not null)
            _onBody(await request.Content.ReadAsStringAsync(cancellationToken));

        var (status, body) = _respond(request);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}




