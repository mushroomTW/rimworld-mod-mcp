import asyncio
import json

from rimworld_mcp.daemon import runner


class _NullWriter:
    """handle() 只需要 close()／wait_closed()，測試不必開真的 socket。"""

    def close(self) -> None:
        return None

    async def wait_closed(self) -> None:
        return None


def _run(lines: list[bytes], token: str, tmp_path, monkeypatch) -> list[dict]:
    monkeypatch.setattr(runner, "data_home", lambda: tmp_path)
    (tmp_path / "bridge-token").write_text(token, encoding="utf-8")
    captured: list[dict] = []
    monkeypatch.setattr(runner, "add_diagnostic", lambda event: captured.append(event))

    async def scenario() -> None:
        reader = asyncio.StreamReader()
        for line in lines:
            reader.feed_data(line)
        reader.feed_eof()
        await runner.handle(reader, _NullWriter())  # type: ignore[arg-type]

    asyncio.run(scenario())
    return captured


def _message(**fields: object) -> bytes:
    return json.dumps(fields).encode("utf-8") + b"\n"


def test_bad_line_does_not_drop_the_rest_of_the_connection(tmp_path, monkeypatch) -> None:
    """Bridge 送出未轉義的控制字元時只該丟掉那一行，後續診斷仍要收到。"""
    captured = _run(
        [
            b"{not valid json at all\n",
            _message(token="secret", type="error", text="first"),
            b'{"token": "secret", "type": "error", "text": "unterminated\n',
            _message(token="secret", type="warning", text="second"),
        ],
        "secret",
        tmp_path,
        monkeypatch,
    )

    assert [event["text"] for event in captured] == ["first", "second"]


def test_wrong_token_is_rejected(tmp_path, monkeypatch) -> None:
    captured = _run([_message(token="guessed", type="error", text="nope")], "secret", tmp_path, monkeypatch)

    assert captured == []


def test_unknown_type_and_non_object_payloads_are_ignored(tmp_path, monkeypatch) -> None:
    captured = _run(
        [
            b"[1, 2, 3]\n",
            b'"just a string"\n',
            _message(token="secret", type="something_else", text="nope"),
            _message(token="secret", type="loaded_mods", text="a,b"),
        ],
        "secret",
        tmp_path,
        monkeypatch,
    )

    assert [event["type"] for event in captured] == ["loaded_mods"]
