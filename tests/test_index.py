from rimworld_mcp.core.index import extract_symbols


def test_extracts_type_and_method() -> None:
    source = """namespace RimWorld
{
    public class Thing
    {
        public void Tick() {}
    }
}"""
    symbols = list(extract_symbols(source, "Assembly-CSharp/Thing.cs"))
    assert any(item.short_name == "Thing" and item.kind == "class" for item in symbols)
    assert any(item.short_name == "Tick" and item.kind == "method" for item in symbols)
