using System.Diagnostics;
using System.Text;
using Xunit;

namespace TrayMon.Tests;

public sealed class ReadmeOutputTests
{
	[Fact]
	public async Task CodeBlocksKeepNestedFencesAndReportAnUnclosedBlock()
	{
		// Load only the parser: checking Markdown must not probe hardware or require a build.
		const string script = """
			$ErrorActionPreference = 'Stop'
			$tokens = $null
			$errors = $null
			$ast = [System.Management.Automation.Language.Parser]::ParseFile(
			    $env:TRAYMON_README_SCRIPT, [ref]$tokens, [ref]$errors)
			if ($errors.Count) { throw ($errors | Out-String) }
			$function = $ast.Find({ param($node)
			    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
			    $node.Name -eq 'Get-CodeBlocks'
			}, $true)
			if (-not $function) { throw 'Get-CodeBlocks missing' }
			. ([scriptblock]::Create($function.Extent.Text))

			$blocks = Get-CodeBlocks @('```text', 'counter in use: test', '```',
			    '~~~text', 'icons: 1 shown of 1', '~~~') fixture.md
			if ($blocks.Count -ne 2 -or $blocks[0][0] -ne 'counter in use: test' -or
			    $blocks[1][0] -ne 'icons: 1 shown of 1') { throw 'Closed blocks lost' }

			$blocks = Get-CodeBlocks @('````text', '```powershell', 'example', '```',
			    '~~~', '`````') fixture.md
			if ($blocks.Count -ne 1 -or $blocks[0].Count -ne 4) { throw 'Nested fences lost' }
			$blocks = Get-CodeBlocks @('No code blocks') fixture.md
			if ($blocks.Count -ne 0) { throw 'Invented a block' }

			$caught = $false
			try { Get-CodeBlocks @('Title', '```text', 'counter in use: test') fixture.md }
			catch {
			    if ($_.Exception.Message -notlike '*fixture.md*незакрытый блок кода*строка 2*') { throw }
			    $caught = $true
			}
			if (-not $caught) { throw 'Unclosed block silently ignored' }
			""";
		var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
			"WindowsPowerShell", "v1.0", "powershell.exe"))
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
			Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
		start.Environment["TRAYMON_README_SCRIPT"] = Path.Combine(AppContext.BaseDirectory, "Check-ReadmeOutput.ps1");
		using var process = Process.Start(start);
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(30_000))
		{
			process.Kill(entireProcessTree: true);
			Assert.Fail("README parser exceeded 30 seconds");
		}
		Assert.True(process.ExitCode == 0, (await output) + (await error));
	}
}
