function Get-BuildDotnet {
    $candidates=@($env:CITA_DOTNET_EXE,(Join-Path $PSScriptRoot 'runtime\dotnet10\dotnet.exe'))
    if($env:DOTNET_ROOT){$candidates+=(Join-Path $env:DOTNET_ROOT 'dotnet.exe')}
    $candidates+=(Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach($candidate in $candidates){
        if(-not $candidate -or -not(Test-Path -LiteralPath $candidate -PathType Leaf)){continue}
        $sdkVersion=& $candidate --version 2>$null
        if($LASTEXITCODE -eq 0 -and $sdkVersion -match '^10\.0\.\d+$'){
            # This only scopes build/test child processes. It never certifies a
            # user SDK as the machine runtime required by the installed service.
            $env:DOTNET_ROOT=Split-Path $candidate
            $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
            return $candidate
        }
    }
    throw 'Install the .NET 10 SDK, or set CITA_DOTNET_EXE to its dotnet.exe.'
}
