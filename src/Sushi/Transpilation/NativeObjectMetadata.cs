namespace Sushi.Transpilation;

/// <summary>Compact keys used only when native objects need runtime metadata.</summary>
internal static class NativeObjectMetadata
{
    public const string Type = "_type";
    public const string EnumName = "_name";
    public const string EnumOrdinal = "_ord";
    public const string EnumValue = "_value";
    public const string MethodPrefix = "_m_";
}
