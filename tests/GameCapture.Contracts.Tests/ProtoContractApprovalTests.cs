using System.Text.Json;
using GameCapture.Contracts.Proto;
using Google.Protobuf.Reflection;
using VerifyXunit;
using Xunit;

namespace GameCapture.Contracts.Tests;

public sealed class ProtoContractApprovalTests
{
    [Fact]
    public Task CaptureProto_Identities_HaveNotChanged()
        => Verifier.Verify(JsonSerializer.Serialize(Describe(FileDescriptor), JsonOptions));

    private static FileDescriptor FileDescriptor => CaptureReflection.Descriptor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static object Describe(FileDescriptor file) => new
    {
        file.Name,
        file.Package,
        Services = file.Services.Select(Describe).ToArray(),
        Messages = file.MessageTypes.Select(Describe).ToArray(),
        Enums = file.EnumTypes.Select(Describe).ToArray(),
    };

    private static object Describe(ServiceDescriptor service) => new
    {
        service.FullName,
        Methods = service.Methods.Select(m => new
        {
            m.Name,
            InputType = m.InputType.FullName,
            OutputType = m.OutputType.FullName,
            m.IsClientStreaming,
            m.IsServerStreaming,
        }).ToArray(),
    };

    private static object Describe(MessageDescriptor message) => new
    {
        message.FullName,
        Fields = message.Fields.InFieldNumberOrder().Select(f => new
        {
            f.FieldNumber,
            f.Name,
            f.JsonName,
            Type = f.FieldType.ToString(),
            MessageType = f.FieldType is FieldType.Message or FieldType.Group
                ? f.MessageType.FullName
                : null,
            EnumType = f.FieldType == FieldType.Enum ? f.EnumType.FullName : null,
            Label = f.IsRepeated ? "Repeated" : "Singular",
            Oneof = f.ContainingOneof?.Name,
        }).ToArray(),
    };

    private static object Describe(EnumDescriptor @enum) => new
    {
        @enum.FullName,
        Values = @enum.Values.Select(v => new
        {
            v.Number,
            v.Name,
        }).ToArray(),
    };
}
