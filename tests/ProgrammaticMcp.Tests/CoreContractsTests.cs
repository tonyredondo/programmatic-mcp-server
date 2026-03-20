using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ProgrammaticMcp.Tests;

public sealed class CoreContractsTests
{
    [Fact]
    public void BuilderRegistrationProducesStableOrderedCatalogSearch()
    {
        var builder = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<ListProjectsInput, ListProjectsResult>(
                "projects.list",
                capability => capability
                    .WithDescription("Lists projects.")
                    .UseWhen("You need to inspect projects.")
                    .DoNotUseWhen("You need to mutate data.")
                    .WithHandler((_, _) => ValueTask.FromResult(new ListProjectsResult(Array.Empty<string>()))))
            .AddCapability<GetProjectInput, GetProjectResult>(
                "projects.getById",
                capability => capability
                    .WithDescription("Gets a project by id.")
                    .UseWhen("You need one project.")
                    .DoNotUseWhen("You need a list.")
                    .WithHandler((input, _) => ValueTask.FromResult(new GetProjectResult(input.ProjectId, "example"))))
            .AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                "tasks.complete",
                mutation => mutation
                    .WithDescription("Completes a task.")
                    .UseWhen("You are sure the task should be completed.")
                    .DoNotUseWhen("You are only exploring.")
                    .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                    .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Complete {args.TaskId}"))
                    .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult(args.TaskId, "done")))));

        var catalog = builder.BuildCatalog();
        var page1 = catalog.Search(new CapabilitySearchRequest(DetailLevel: CapabilityDetailLevel.Names, Limit: 2));
        var page2 = catalog.Search(new CapabilitySearchRequest(DetailLevel: CapabilityDetailLevel.Full, Limit: 2, Cursor: page1.NextCursor));

        Assert.Equal(new[] { "projects.getById", "projects.list" }, page1.Items.Select(item => item.ApiPath));
        Assert.Equal("tasks.complete", Assert.Single(page2.Items).ApiPath);
        Assert.Equal(CapabilityDetailLevel.Names, page1.DetailLevel);
        Assert.Equal(CapabilityDetailLevel.Full, page2.DetailLevel);
        Assert.NotNull(page1.NextCursor);
        Assert.Null(page2.NextCursor);
        Assert.All(page1.Items, item =>
        {
            Assert.Null(item.Signature);
            Assert.Null(item.Description);
            Assert.Null(item.ResultSchema);
            Assert.Null(item.Guidance);
        });
        Assert.NotNull(page2.Items[0].InputSchema);
        Assert.NotNull(page2.Items[0].ResultSchema);
        Assert.NotNull(page2.Items[0].PreviewPayloadSchema);
        Assert.NotNull(page2.Items[0].ApplyResultSchema);
        Assert.Contains("You are sure the task should be completed.", page2.Items[0].Guidance!.UseWhen);
    }

    [Fact]
    public void DangerousApiPathSegmentsAreRejected()
    {
        var builder = new ProgrammaticMcpBuilder();

        builder.AddCapability<ListProjectsInput, ListProjectsResult>(
            "projects.__proto__",
            capability => capability
                .WithDescription("bad")
                .UseWhen("never")
                .DoNotUseWhen("always")
                .WithHandler((_, _) => ValueTask.FromResult(new ListProjectsResult(Array.Empty<string>()))));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.BuildCatalog());
        Assert.Contains("__proto__", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationsRequireExplicitAuthorizationChoice()
    {
        var builder = new ProgrammaticMcpBuilder();
        builder.AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
            "tasks.complete",
            mutation => mutation
                .WithDescription("Completes a task.")
                .UseWhen("You are sure the task should be completed.")
                .DoNotUseWhen("You are only exploring.")
                .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Complete {args.TaskId}"))
                .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult(args.TaskId, "done")))));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.BuildCatalog());
        Assert.Contains("authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaGenerationIsDeterministicAndUsesJsonSchema202012()
    {
        var generator = new BuiltInSchemaGenerator();

        var schema1 = generator.Generate(typeof(SchemaFixture));
        var schema2 = generator.Generate(typeof(SchemaFixture));

        Assert.Equal(ProgrammaticContractConstants.JsonSchemaDialect, schema1["$schema"]?.GetValue<string>());
        Assert.Equal(CanonicalJson.Serialize(schema1), CanonicalJson.Serialize(schema2));
        Assert.Equal("date-time", schema1["properties"]?["createdAt"]?["format"]?.GetValue<string>());
        Assert.Equal("uuid", schema1["properties"]?["id"]?["format"]?.GetValue<string>());
        Assert.Equal("date", schema1["properties"]?["dueDate"]?["format"]?.GetValue<string>());
        Assert.Equal("time", schema1["properties"]?["cutoff"]?["format"]?.GetValue<string>());
        Assert.Equal("c", schema1["properties"]?["duration"]?["x-dotnet-format"]?.GetValue<string>());
    }

    [Fact]
    public void SchemaGenerationAndValidationSupportRepeatedNestedObjectTypes()
    {
        var generator = new BuiltInSchemaGenerator();

        var schema = generator.Generate(typeof(RepeatedContainer));
        var payload = JsonNode.Parse("""{"primary":{"name":"a","leaf":{"count":1}},"secondary":{"name":"b","leaf":{"count":2}}}""");

        Assert.Contains("\"$ref\"", schema.ToJsonString(), StringComparison.Ordinal);
        JsonSchemaValidator.Validate(payload, schema);
    }

    [Fact]
    public void SchemaGenerationResolvesDefinitionNameCollisionsDeterministically()
    {
        var generator = new BuiltInSchemaGenerator();

        var schema = generator.Generate(typeof(CollidingDefinitionContainer));
        var definitions = schema["$defs"]!.AsObject();

        Assert.True(definitions.ContainsKey("Node"));
        Assert.True(definitions.ContainsKey("Node2"));
        Assert.NotEqual(
            CanonicalJson.Serialize(definitions["Node"]),
            CanonicalJson.Serialize(definitions["Node2"]));
        Assert.Equal("#/$defs/Node", schema["properties"]!["left1"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/Node", schema["properties"]!["left2"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/Node2", schema["properties"]!["right1"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/$defs/Node2", schema["properties"]!["right2"]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public void SchemaGenerationSupportsNullableReferenceTypesAndConditionalJsonIgnoreProperties()
    {
        var generator = new BuiltInSchemaGenerator();

        var nullableSchema = generator.Generate(typeof(NullableReferenceFixture));
        var nullableProperties = nullableSchema["properties"]!.AsObject();
        var nullableRequired = nullableSchema["required"]?.AsArray().Select(static item => item!.GetValue<string>()) ?? Array.Empty<string>();

        Assert.Contains("anyOf", nullableProperties["note"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("anyOf", nullableProperties["values"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("anyOf", nullableProperties["names"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("note", nullableRequired);
        Assert.DoesNotContain("values", nullableRequired);
        Assert.DoesNotContain("names", nullableRequired);

        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{"note":null,"values":null,"names":["alpha",null]}"""),
            nullableSchema);
        JsonSchemaValidator.Validate(JsonNode.Parse("{}"), nullableSchema);

        var ignoreSchema = generator.Generate(typeof(JsonIgnoreFixture));
        var ignoreProperties = ignoreSchema["properties"]!.AsObject();
        var ignoreRequired = ignoreSchema["required"]?.AsArray().Select(static item => item!.GetValue<string>()) ?? Array.Empty<string>();

        Assert.True(ignoreProperties.ContainsKey("visible"));
        Assert.True(ignoreProperties.ContainsKey("writeOnly"));
        Assert.False(ignoreProperties.ContainsKey("hidden"));
        Assert.Contains("writeOnly", ignoreRequired);
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{"visible":"hello","writeOnly":"value"}"""),
            ignoreSchema);
    }

    [Fact]
    public void SchemaGenerationTreatsDictionaryStringValuesAsObjectMaps()
    {
        var generator = new BuiltInSchemaGenerator();

        var schema = generator.Generate(typeof(DictionaryContainer));
        var valuesSchema = schema["properties"]!["values"]!.AsObject();

        Assert.Equal("object", valuesSchema["type"]!.GetValue<string>());
        Assert.False(valuesSchema.ContainsKey("items"));
        Assert.Equal("integer", valuesSchema["additionalProperties"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void UnsupportedAutomaticSchemaCasesRequireOverride()
    {
        var generator = new BuiltInSchemaGenerator();

        Assert.Throws<UnsupportedSchemaTypeException>(() => generator.Generate(typeof(UnsupportedFixture)));
    }

    [Fact]
    public void RuntimeValidationRejectsInvalidPayloads()
    {
        var generator = new BuiltInSchemaGenerator();
        var schema = generator.Generate(typeof(ListProjectsInput));

        var valid = JsonNode.Parse("""{"includeArchived":true}""");
        JsonSchemaValidator.Validate(valid, schema);

        var invalid = JsonNode.Parse("""{"includeArchived":"yes"}""");
        var exception = Assert.Throws<JsonSchemaValidationException>(() => JsonSchemaValidator.Validate(invalid, schema));
        Assert.Contains("boolean", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedTypeScriptAndCapabilityVersionAreDeterministic()
    {
        var first = CreateDeterministicCatalog("Lists projects.");
        var second = CreateDeterministicCatalog("Lists projects.");
        var changed = CreateDeterministicCatalog("Lists all projects.");

        Assert.Equal(first.GeneratedTypeScript, second.GeneratedTypeScript);
        Assert.Equal(first.CapabilityVersion, second.CapabilityVersion);
        Assert.NotEqual(first.CapabilityVersion, changed.CapabilityVersion);
        Assert.Contains("type ProjectsListInput =", first.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("type TasksCompleteApplyResult =", first.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("projects.list(input: ProjectsListInput) -> Promise<ProjectsListResult>", first.Capabilities[0].Signature, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedTypeScriptAndCapabilityVersionMatchGoldenOutputs()
    {
        var catalog = CreateDeterministicCatalog("Lists projects.");
        var expectedDeclarations = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "DeterministicCatalog.d.ts"));
        var expectedCapabilityVersion = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "DeterministicCatalog.capability-version.txt")).Trim();
        var expectedArgsHash = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "DeterministicCatalog.args-hash.txt")).Trim();
        var args = JsonNode.Parse("""{"taskId":"task-1"}""")!.AsObject();

        Assert.Equal(expectedDeclarations.ReplaceLineEndings("\n"), catalog.GeneratedTypeScript.ReplaceLineEndings("\n"));
        Assert.Equal(expectedCapabilityVersion, catalog.CapabilityVersion);
        Assert.Equal(expectedArgsHash, CapabilityVersionCalculator.CalculateArgsHash(args));
    }

    [Fact]
    public void GeneratedTypeScriptResolvesDefinitionsAndCollisionNamesConsistently()
    {
        var catalog = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<RepeatedContainer, RepeatedContainer>(
                "foo.bar",
                capability => capability
                    .WithDescription("First colliding capability.")
                    .UseWhen("You need the first capability.")
                    .DoNotUseWhen("You need the second capability.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .AddCapability<RepeatedContainer, RepeatedContainer>(
                "fooBar",
                capability => capability
                    .WithDescription("Second colliding capability.")
                    .UseWhen("You need the second capability.")
                    .DoNotUseWhen("You need the first capability.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .BuildCatalog();

        Assert.Contains("type FooBarInputRepeatedNode =", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("primary: FooBarInputRepeatedNode", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("function bar(input: FooBarInput): Promise<FooBarResult>;", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("function fooBar(input: FooBarInput2): Promise<FooBarResult2>;", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Equal("foo.bar(input: FooBarInput) -> Promise<FooBarResult>", catalog.Capabilities[0].Signature);
        Assert.Equal("fooBar(input: FooBarInput2) -> Promise<FooBarResult2>", catalog.Capabilities[1].Signature);
    }

    [Fact]
    public void GeneratedTypeScriptCollisionSuffixesFollowRegistrationOrder()
    {
        var catalog = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<RepeatedContainer, RepeatedContainer>(
                "fooBar",
                capability => capability
                    .WithDescription("Registered first.")
                    .UseWhen("You need the first capability.")
                    .DoNotUseWhen("You need the second capability.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .AddCapability<RepeatedContainer, RepeatedContainer>(
                "foo.bar",
                capability => capability
                    .WithDescription("Registered second.")
                    .UseWhen("You need the second capability.")
                    .DoNotUseWhen("You need the first capability.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .BuildCatalog();

        Assert.Contains("function fooBar(input: FooBarInput): Promise<FooBarResult>;", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("function bar(input: FooBarInput2): Promise<FooBarResult2>;", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Equal("foo.bar(input: FooBarInput2) -> Promise<FooBarResult2>", catalog.Capabilities[0].Signature);
        Assert.Equal("fooBar(input: FooBarInput) -> Promise<FooBarResult>", catalog.Capabilities[1].Signature);
    }

    [Fact]
    public void GeneratedTypeScriptParenthesizesNullableArrayItems()
    {
        var catalog = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<ArrayFixture, ArrayFixture>(
                "values.echo",
                capability => capability
                    .WithDescription("Echoes nullable array values.")
                    .UseWhen("You need nullable values back.")
                    .DoNotUseWhen("You need anything else.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .BuildCatalog();

        Assert.Contains("(number | null)[]", catalog.GeneratedTypeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void FullDetailSearchResultsAreDetachedFromCatalogState()
    {
        var catalog = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<SearchExampleInput, SearchExampleResult>(
                "items.get",
                capability => capability
                    .WithDescription("Gets an item.")
                    .UseWhen("You need one item.")
                    .DoNotUseWhen("You need a list.")
                    .AddExample("Example item", new SearchExampleInput("a"), new SearchExampleResult("b"))
                    .WithHandler((input, _) => ValueTask.FromResult(new SearchExampleResult(input.Id))))
            .BuildCatalog();

        var first = catalog.Search(new CapabilitySearchRequest(DetailLevel: CapabilityDetailLevel.Full, Limit: 1));
        first.Items[0].Examples[0].Input!["id"] = "mutated";
        ((string[])first.Items[0].Guidance!.UseWhen)[0] = "mutated";

        var second = catalog.Search(new CapabilitySearchRequest(DetailLevel: CapabilityDetailLevel.Full, Limit: 1));

        Assert.Equal("a", second.Items[0].Examples[0].Input!["id"]!.GetValue<string>());
        Assert.Equal("You need one item.", second.Items[0].Guidance!.UseWhen[0]);
    }

    [Fact]
    public void GeneratedTypeScriptQuotesNonIdentifierJsonPropertyNames()
    {
        var catalog = new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<AliasedPropertyInput, AliasedPropertyResult>(
                "tasks.alias",
                capability => capability
                    .WithDescription("Uses non-identifier JSON property names.")
                    .UseWhen("You need to verify generated TypeScript alignment.")
                    .DoNotUseWhen("You are testing other schema paths.")
                    .WithHandler((input, _) => ValueTask.FromResult(new AliasedPropertyResult(input.TaskId))))
            .BuildCatalog();

        Assert.Contains("\"task-id\": string", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("\"status-code\": string", catalog.GeneratedTypeScript, StringComparison.Ordinal);
        Assert.Contains("\"task-id\"", catalog.Capabilities[0].Input.Schema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"status-code\"", catalog.Capabilities[0].Result.Schema!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogBuildRejectsDuplicateAndNamespaceCollidingApiPaths()
    {
        var duplicate = new ProgrammaticMcpBuilder()
            .AddCapability<EmptyInput, EmptyInput>(
                "tasks.list",
                capability => capability
                    .WithDescription("First.")
                    .UseWhen("You need the first one.")
                    .DoNotUseWhen("You need the second one.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .AddCapability<EmptyInput, EmptyInput>(
                "tasks.list",
                capability => capability
                    .WithDescription("Second.")
                    .UseWhen("You need the second one.")
                    .DoNotUseWhen("You need the first one.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)));

        var collision = new ProgrammaticMcpBuilder()
            .AddCapability<EmptyInput, EmptyInput>(
                "tasks",
                capability => capability
                    .WithDescription("Leaf.")
                    .UseWhen("You need the leaf.")
                    .DoNotUseWhen("You need the namespace.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)))
            .AddCapability<EmptyInput, EmptyInput>(
                "tasks.list",
                capability => capability
                    .WithDescription("Namespace child.")
                    .UseWhen("You need the child.")
                    .DoNotUseWhen("You need the leaf.")
                    .WithHandler((input, _) => ValueTask.FromResult(input)));

        Assert.Throws<InvalidOperationException>(() => duplicate.BuildCatalog());
        Assert.Throws<InvalidOperationException>(() => collision.BuildCatalog());
    }

    [Fact]
    public void SharedExecutionAndEnvelopeContractsReflectPlannedWireShapes()
    {
        var preview = CreateApproval().PreviewEnvelope;
        var result = new CodeExecutionResult(
            ProgrammaticContractConstants.SchemaVersion,
            "cap-v1",
            JsonNode.Parse("""{"ok":true}"""),
            new[] { new ExecutionConsoleEntry("info", "hello") },
            new[] { new ExecutionDiagnostic("result_spilled_to_artifact", "spilled") },
            new[] { new ExecutionArtifactDescriptor("artifact-1", "execution.result", "result.json", "application/json", 42, 1, "2026-03-19T00:00:00Z") },
            new[] { preview },
            "artifact-1",
            new[] { "tasks.complete" },
            new ExecutionStats(1, 25, 10, 1024, 1));

        Assert.Equal(ProgrammaticContractConstants.SchemaVersion, result.SchemaVersion);
        Assert.Equal("cap-v1", result.CapabilityVersion);
        Assert.Single(result.ApprovalsRequested);
        Assert.Equal(1, result.Stats.ApiCalls);
        Assert.Equal(25, result.Stats.ElapsedMs);
        Assert.Equal(42, result.Artifacts[0].TotalBytes);
        Assert.Equal(1, result.Artifacts[0].TotalChunks);
        Assert.Equal("execution.result", result.Artifacts[0].Kind);
    }

    [Fact]
    public void CanonicalJsonNormalizesNumericVectors()
    {
        Assert.Equal("0", CanonicalJson.NormalizeNumber("-0"));
        Assert.Equal("0", CanonicalJson.NormalizeNumber("-0.0"));
        Assert.Equal("1.23", CanonicalJson.NormalizeNumber("1.2300"));
        Assert.Equal("1000", CanonicalJson.NormalizeNumber("1e+3"));
        Assert.Equal("0.000001", CanonicalJson.NormalizeNumber("0.000001"));
        Assert.Equal("333333333.33333329", CanonicalJson.NormalizeNumber("333333333.33333329"));
        Assert.Equal("1e-27", CanonicalJson.NormalizeNumber("0.000000000000000000000000001"));
        Assert.Equal("9007199254740993", CanonicalJson.NormalizeNumber("9007199254740993"));
        Assert.Equal("-9007199254740993", CanonicalJson.NormalizeNumber("-9007199254740993"));
    }

    [Fact]
    public void CanonicalJsonPreservesDistinctLargeIntegers()
    {
        var first = JsonNode.Parse("9007199254740992");
        var second = JsonNode.Parse("9007199254740993");

        Assert.NotEqual(CanonicalJson.Serialize(first), CanonicalJson.Serialize(second));
        Assert.NotEqual(CanonicalJson.Sha256(first), CanonicalJson.Sha256(second));
    }

    [Fact]
    public void MutationListContractOmitsApprovalNonce()
    {
        var response = new MutationListResponse(
            ProgrammaticContractConstants.SchemaVersion,
            "cap-v1",
            new[]
            {
                new MutationListItem(
                    "mutation_preview",
                    "approval-1",
                    "tasks.complete",
                    "Complete task-1",
                    JsonNode.Parse("""{"taskId":"task-1"}""")!.AsObject(),
                    JsonNode.Parse("""{"taskId":"task-1","willComplete":true}"""),
                    "args-hash",
                    "2026-03-19T00:00:00Z")
            },
            null);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.SerializeToNode(response, options)!.AsObject();
        var item = Assert.Single(serialized["items"]!.AsArray())!.AsObject();

        Assert.False(item.ContainsKey("approvalNonce"));
    }

    [Fact]
    public async Task ApprovalStoreTransitionsAreAtomicAndNonceFormatIsValid()
    {
        var approval = CreateApproval();
        var store = new InMemoryApprovalStore();
        await store.CreateAsync(approval);

        var results = await Task.WhenAll(
            store.TryTransitionAsync(
                approval.ApprovalId,
                ApprovalState.Pending,
                current => current with { State = ApprovalState.Applying, ApplyingSinceUtc = DateTimeOffset.UtcNow }).AsTask(),
            store.TryTransitionAsync(
                approval.ApprovalId,
                ApprovalState.Pending,
                current => current with { State = ApprovalState.Cancelled }).AsTask());

        Assert.Equal(1, results.Count(result => result.Status == ApprovalTransitionStatus.Success));
        Assert.Equal(1, results.Count(result => result.Status == ApprovalTransitionStatus.UnexpectedState));
        Assert.Equal(22, approval.ApprovalNonce.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", approval.ApprovalNonce);
        Assert.True(Guid.TryParse(approval.ApprovalId, out _));
    }

    [Fact]
    public async Task ArtifactStoreEnforcesQuotasAndSweepsExpiredEntriesUnderLoad()
    {
        var options = new ArtifactRetentionOptions(
            ArtifactTtlSeconds: 60,
            MaxArtifactBytesPerArtifact: 16,
            MaxArtifactsPerConversation: 3,
            MaxArtifactBytesPerConversation: 23,
            MaxArtifactBytesGlobal: 32,
            ArtifactChunkBytes: 4);
        var store = new InMemoryArtifactStore(options);

        await store.WriteAsync(new ArtifactWriteRequest("artifact-1", "conv-1", "caller-1", "execution.result", "a.txt", "text/plain", "12345678", DateTimeOffset.UtcNow.AddMinutes(1)));
        await store.WriteAsync(new ArtifactWriteRequest("artifact-2", "conv-1", "caller-1", "execution.result", "b.txt", "text/plain", "abcdefgh", DateTimeOffset.UtcNow.AddMinutes(1)));

        var perConversation = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            new ArtifactWriteRequest("artifact-3", "conv-1", "caller-1", "execution.result", "c.txt", "text/plain", "ABCDEFGHI", DateTimeOffset.UtcNow.AddMinutes(1))).AsTask());
        Assert.Contains("artifact byte limit", perConversation.Message, StringComparison.OrdinalIgnoreCase);

        await store.WriteAsync(new ArtifactWriteRequest("artifact-4", "conv-2", "caller-2", "execution.result", "d.txt", "text/plain", "ijklmnop", DateTimeOffset.UtcNow.AddMinutes(1)));
        var global = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            new ArtifactWriteRequest("artifact-5", "conv-3", "caller-3", "execution.result", "e.txt", "text/plain", "QRSTUVWXY", DateTimeOffset.UtcNow.AddMinutes(1))).AsTask());
        Assert.Contains("global byte limit", global.Message, StringComparison.OrdinalIgnoreCase);

        await store.WriteAsync(new ArtifactWriteRequest("artifact-expired", "conv-expired", "caller-expired", "execution.result", "expired.txt", "text/plain", "gone", DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.SweepExpiredAsync();

        var expired = await store.ReadAsync(new ArtifactReadRequest("artifact-expired", "conv-expired", "caller-expired"));
        Assert.False(expired.Found);
    }

    [Fact]
    public async Task ArtifactStorePagesReadsThroughTheCoreCursorContract()
    {
        var options = new ArtifactRetentionOptions(
            ArtifactTtlSeconds: 60,
            MaxArtifactBytesPerArtifact: 64,
            MaxArtifactsPerConversation: 3,
            MaxArtifactBytesPerConversation: 128,
            MaxArtifactBytesGlobal: 256,
            ArtifactChunkBytes: 4);
        var store = new InMemoryArtifactStore(options);

        await store.WriteAsync(new ArtifactWriteRequest(
            "artifact-1",
            "conv-1",
            "caller-1",
            "execution.result",
            "artifact.txt",
            "text/plain",
            "abcdefghijkl",
            DateTimeOffset.UtcNow.AddMinutes(1)));

        var page1 = await store.ReadAsync(new ArtifactReadRequest("artifact-1", "conv-1", "caller-1", Limit: 2));
        Assert.True(page1.Found);
        Assert.Equal(2, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);
        Assert.Equal(3, page1.TotalChunks);

        var page2 = await store.ReadAsync(new ArtifactReadRequest("artifact-1", "conv-1", "caller-1", page1.NextCursor, 2));
        Assert.True(page2.Found);
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);
        Assert.Equal("ijkl", page2.Items[0].Content);
    }

    [Fact]
    public async Task ApprovalStoreRecoversStaleApplyingEntriesAndSweepsExpiredOnes()
    {
        var store = new InMemoryApprovalStore();
        var stale = CreateApproval() with
        {
            ApprovalId = "00000000-0000-0000-0000-000000000001",
            State = ApprovalState.Applying,
            ApplyingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var expired = CreateApproval() with
        {
            ApprovalId = "00000000-0000-0000-0000-000000000002",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        await store.CreateAsync(stale);
        await store.CreateAsync(expired);

        var recovered = await store.RecoverStaleApplyingAsync(
            TimeSpan.FromSeconds(30),
            approval => approval with
            {
                State = ApprovalState.FailedTerminal,
                ApplyingSinceUtc = null,
                FailureCode = "apply_outcome_unknown"
            });

        Assert.Equal(1, recovered);

        var recoveredApproval = await store.GetAsync(stale.ApprovalId);
        Assert.NotNull(recoveredApproval);
        Assert.Equal(ApprovalState.FailedTerminal, recoveredApproval!.State);
        Assert.Equal("apply_outcome_unknown", recoveredApproval.FailureCode);

        await store.SweepExpiredAsync();
        Assert.Null(await store.GetAsync(expired.ApprovalId));
    }

    [Fact]
    public void ConversationIdValidationAndRootObjectRequirementAreEnforced()
    {
        Assert.True(ConversationIdValidator.IsValid("conv-123_ABC"));
        Assert.False(ConversationIdValidator.IsValid("bad value"));
        Assert.True(SchemaVersionValidator.IsSupported(ProgrammaticContractConstants.SchemaVersion));
        Assert.Throws<InvalidOperationException>(() => SchemaVersionValidator.EnsureSupported(99));

        var cursor = CursorCodec.CreateOffsetCursor(5, "cap-v1");
        Assert.Equal(5, CursorCodec.ParseOffset(cursor, "cap-v1"));
        Assert.Throws<InvalidOperationException>(() => CursorCodec.ParseOffset(cursor, "cap-v2"));

        var builder = new ProgrammaticMcpBuilder();
        builder.AddCapability<int, ListProjectsResult>(
            "numbers.bad",
            capability => capability
                .WithDescription("bad")
                .UseWhen("never")
                .DoNotUseWhen("always")
                .WithHandler((_, _) => ValueTask.FromResult(new ListProjectsResult(Array.Empty<string>()))));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.BuildCatalog());
        Assert.Contains("root schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProgrammaticCatalogSnapshot CreateDeterministicCatalog(string description)
    {
        return new ProgrammaticMcpBuilder()
            .AllowAllBoundCallers()
            .AddCapability<ListProjectsInput, ListProjectsResult>(
                "projects.list",
                capability => capability
                    .WithDescription(description)
                    .UseWhen("You need to inspect projects.")
                    .DoNotUseWhen("You need to mutate data.")
                    .WithHandler((_, _) => ValueTask.FromResult(new ListProjectsResult(Array.Empty<string>()))))
            .AddMutation<CompleteTaskArgs, CompleteTaskPreview, CompleteTaskApplyResult>(
                "tasks.complete",
                mutation => mutation
                    .WithDescription("Completes a task.")
                    .UseWhen("You are sure the task should be completed.")
                    .DoNotUseWhen("You are only exploring.")
                    .WithPreviewHandler((args, _) => ValueTask.FromResult(new CompleteTaskPreview(args.TaskId, true)))
                    .WithSummaryFactory((args, _, _) => ValueTask.FromResult($"Complete {args.TaskId}"))
                    .WithApplyHandler((args, _) => ValueTask.FromResult(MutationApplyResult<CompleteTaskApplyResult>.Success(new CompleteTaskApplyResult(args.TaskId, "done")))))
            .BuildCatalog();
    }

    private static PendingApproval CreateApproval()
    {
        var args = JsonNode.Parse("""{"taskId":"task-1"}""")!.AsObject();
        return new PendingApproval(
            ApprovalTokenGenerator.GenerateApprovalId(),
            ApprovalTokenGenerator.GenerateApprovalNonce(),
            "tasks.complete",
            args,
            CapabilityVersionCalculator.CalculateArgsHash(args),
            new MutationPreviewEnvelope(
                "mutation_preview",
                ApprovalTokenGenerator.GenerateApprovalId(),
                ApprovalTokenGenerator.GenerateApprovalNonce(),
                "tasks.complete",
                "Complete task-1",
                args,
                JsonNode.Parse("""{"taskId":"task-1","willComplete":true}"""),
                CapabilityVersionCalculator.CalculateArgsHash(args),
                DateTimeOffset.UtcNow.AddMinutes(10).ToString("O")),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            "conv-1",
            "caller-1",
            ApprovalState.Pending,
            null,
            null);
    }

    public sealed record ListProjectsInput(bool IncludeArchived);

    public sealed record ListProjectsResult(string[] Projects);

    public sealed record GetProjectInput(string ProjectId);

    public sealed record GetProjectResult(string ProjectId, string Name);

    public sealed record CompleteTaskArgs(string TaskId);

    public sealed record CompleteTaskPreview(string TaskId, bool WillComplete);

    public sealed record CompleteTaskApplyResult(string TaskId, string Status);

    public sealed record EmptyInput();

    public sealed record SchemaFixture(
        Guid Id,
        DateTimeOffset CreatedAt,
        DateOnly DueDate,
        TimeOnly Cutoff,
        TimeSpan Duration,
        Uri Url,
        NestedFixture Nested,
        IReadOnlyList<int> Values,
        IReadOnlyDictionary<string, string> Metadata,
        string? OptionalNote);

    public sealed record NullableReferenceFixture(
        string? Note,
        IReadOnlyList<int>? Values,
        IReadOnlyList<string?>? Names);

    public sealed record NestedFixture(string Name);

    public sealed record RepeatedContainer(RepeatedNode Primary, RepeatedNode Secondary);

    public sealed record RepeatedNode(string Name, RepeatedLeaf Leaf);

    public sealed record RepeatedLeaf(int Count);

    public sealed record DictionaryContainer(Dictionary<string, int> Values);

    public sealed record AliasedPropertyInput([property: JsonPropertyName("task-id")] string TaskId);

    public sealed record AliasedPropertyResult([property: JsonPropertyName("status-code")] string StatusCode);

    public sealed record JsonIgnoreFixture(
        string Visible,
        [property: JsonIgnore] string Hidden,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string WriteOnly);

    public sealed record ArrayFixture(int?[] Values);

    public sealed record SearchExampleInput(string Id);

    public sealed record SearchExampleResult(string Id);

    public sealed record CollidingDefinitionContainer(
        OuterAlpha.Node Left1,
        OuterAlpha.Node Left2,
        OuterBeta.Node Right1,
        OuterBeta.Node Right2);

    public static class OuterAlpha
    {
        public sealed record Node(string Name, SharedLeaf Leaf);
    }

    public static class OuterBeta
    {
        public sealed record Node(int Count, SharedLeaf Leaf);
    }

    public sealed record SharedLeaf(string Value);

    public sealed record UnsupportedFixture(JsonElement Raw);
}
