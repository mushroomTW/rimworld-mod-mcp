"""實際執行 C# 的 DiagnosticPayload.Escape，驗證 Python 這一側吃得下它的輸出。

Bridge 與 daemon 之間的接縫是跨語言的：C# 手工組 JSON，Python 用 json.loads 解。
只有「編譯過」不構成證據——漏轉義一個控制字元，daemon 就會丟掉整筆診斷。
這裡透過 bridge/Tools/EscapeProbe 這個不相依 RimWorld 的小程式真跑一次逐字元邏輯。
"""

import asyncio
import base64
import json
import shutil
import subprocess
from pathlib import Path

import pytest

from rimworld_mcp.daemon import runner

REPO = Path(__file__).resolve().parents[1]
PROBE_PROJECT = REPO / "bridge" / "Tools" / "EscapeProbe" / "EscapeProbe.csproj"
PROBE_DLL = REPO / "bridge" / "Tools" / "EscapeProbe" / "bin" / "Release" / "net8.0" / "EscapeProbe.dll"

pytestmark = pytest.mark.skipif(
    shutil.which("dotnet") is None, reason="需要 .NET SDK 才能實際執行 C# 的 Escape"
)

ALL_C0_CONTROLS = "".join(chr(code) for code in range(0x20))

NASTY_INPUTS = [
    "plain ascii",
    "",
    "tab\there",
    "crlf\r\nnewline",
    'quote " backslash \\ both \\"',
    ALL_C0_CONTROLS,
    "delete\x7fchar",
    "中文與 emoji \U0001f600",
    '"}{"type":"injected","token":"stolen"',
    "trailing backslash \\",
]


@pytest.fixture(scope="session")
def probe() -> Path:
    build = subprocess.run(
        ["dotnet", "build", str(PROBE_PROJECT), "-c", "Release", "--nologo", "-v", "q"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    assert build.returncode == 0, f"probe 建置失敗：\n{build.stdout}\n{build.stderr}"
    assert PROBE_DLL.is_file(), f"找不到 {PROBE_DLL}"
    return PROBE_DLL


def _lines_from_csharp(probe: Path, inputs: list[str]) -> list[str]:
    """把輸入以 base64 餵給 probe，取回真正由 C# 產生的 NDJSON 行。"""
    payload = "".join(base64.b64encode(item.encode("utf-8")).decode("ascii") + "\n" for item in inputs)
    run = subprocess.run(
        ["dotnet", str(probe)],
        input=payload,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="strict",
        check=False,
    )
    assert run.returncode == 0, f"probe 執行失敗：{run.stderr}"
    lines = [line for line in run.stdout.splitlines() if line.startswith("{")]
    assert len(lines) == len(inputs), f"期望 {len(inputs)} 行，實得 {len(lines)}"
    return lines


def test_csharp_output_is_valid_json_and_round_trips_losslessly(probe: Path) -> None:
    """缺口 4 的核心：C# 產出的每一行都必須能被 json.loads 解析，且內容無損。"""
    lines = _lines_from_csharp(probe, NASTY_INPUTS)

    for original, line in zip(NASTY_INPUTS, lines, strict=True):
        message = json.loads(line)
        assert message["text"] == original, f"內容遺失：{original!r} -> {message['text']!r}"
        assert message["type"] == "error"
        assert message["token"] == "test-token"
        assert message["first_line"] == "probe"


def test_text_field_cannot_break_out_of_the_json_envelope(probe: Path) -> None:
    """log 內容若能跳脫字串，就能偽造 type／token 欄位。"""
    payload = '"}{"type":"injected","token":"stolen","text":"pwned'
    (line,) = _lines_from_csharp(probe, [payload])

    message = json.loads(line)

    assert message["type"] == "error"
    assert message["token"] == "test-token"
    assert message["text"] == payload
    assert line.count('"type"') == 1


def test_csharp_output_survives_the_daemon_read_path(probe: Path, tmp_path, monkeypatch) -> None:
    """把真實 C# 輸出餵進 daemon 的 handle()，證明兩側接縫真的對得上。"""
    inputs = ["tab\there", ALL_C0_CONTROLS, "中文 \U0001f600", 'quote " backslash \\']
    lines = _lines_from_csharp(probe, inputs)

    monkeypatch.setattr(runner, "data_home", lambda: tmp_path)
    (tmp_path / "bridge-token").write_text("test-token", encoding="utf-8")
    captured: list[dict] = []
    monkeypatch.setattr(runner, "add_diagnostic", lambda event: captured.append(event))

    class _NullWriter:
        def close(self) -> None:
            return None

        async def wait_closed(self) -> None:
            return None

    async def scenario() -> None:
        reader = asyncio.StreamReader()
        for line in lines:
            reader.feed_data(line.encode("utf-8") + b"\n")
        reader.feed_eof()
        await runner.handle(reader, _NullWriter())  # type: ignore[arg-type]

    asyncio.run(scenario())

    assert [event["text"] for event in captured] == inputs


def test_unescaped_control_character_would_be_rejected(probe: Path) -> None:
    """記錄 Escape 存在的理由：原始控制字元放進 JSON 字串就是非法的。

    這一條確保 test_csharp_output_is_valid_json_... 不是碰巧通過——
    若 C# 端漏掉 tab 的轉義，daemon 就會像這樣拒收。
    """
    naive = '{"type":"error","token":"test-token","first_line":"probe","text":"tab\there"}'

    with pytest.raises(json.JSONDecodeError, match="Invalid control character"):
        json.loads(naive)

    (escaped,) = _lines_from_csharp(probe, ["tab\there"])
    assert json.loads(escaped)["text"] == "tab\there"
