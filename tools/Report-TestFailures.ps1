# Turns failed tests in TRX results into GitHub error annotations, so a
# failing run can be diagnosed from the checks view without the full log.
param([string]$ResultsDirectory = 'TestResults')

$failed = 0
Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    [xml]$trx = Get-Content $_.FullName
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    Select-Xml -Xml $trx -XPath '//t:UnitTestResult[@outcome="Failed"]' -Namespace $ns | ForEach-Object {
        $result = $_.Node
        $message = ($result.Output.ErrorInfo.Message -split "`n" | Select-Object -First 3) -join ' '
        $message = $message -replace '[\r\n]', ' '
        Write-Output "::error title=$($result.testName)::$message"
        $failed++
    }
}
Write-Output "Failed tests: $failed"
