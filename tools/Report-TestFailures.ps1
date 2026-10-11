# Turns failed tests in TRX results into GitHub error annotations, so a
# failing run can be diagnosed from the checks view without the full log.
param([string]$ResultsDirectory = 'TestResults')

$ErrorActionPreference = 'Continue'
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$files = @(Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Output "::error title=No test results::No TRX files under $ResultsDirectory; the failure happened before tests reported (build, restore or test host)."
}
foreach ($file in $files) {
    try {
        [xml]$trx = Get-Content -Raw $file.FullName
        $counters = (Select-Xml -Xml $trx -XPath '//t:Counters' -Namespace $ns).Node
        if ($counters) {
            Write-Output "$($file.Name): total $($counters.total), passed $($counters.passed), failed $($counters.failed), error $($counters.error)"
        }
        foreach ($match in @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult[@outcome!="Passed" and @outcome!="NotExecuted"]' -Namespace $ns)) {
            $result = $match.Node
            $message = "$($result.Output.ErrorInfo.Message)" -replace '[\r\n]+', ' '
            if ($message.Length -gt 400) { $message = $message.Substring(0, 400) }
            Write-Output "::error title=$($result.testName)::$($result.outcome): $message"
        }
        foreach ($match in @(Select-Xml -Xml $trx -XPath '//t:RunInfo[@outcome="Error"]' -Namespace $ns)) {
            $text = "$($match.Node.Text)" -replace '[\r\n]+', ' '
            if ($text.Length -gt 400) { $text = $text.Substring(0, 400) }
            Write-Output "::error title=Test run error ($($file.Name))::$text"
        }
    } catch {
        Write-Output "::error title=Could not read $($file.Name)::$($_.Exception.Message)"
    }
}
exit 0
