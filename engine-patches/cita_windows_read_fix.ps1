function Apply-CitaWindowsReadFix([string]$ModuleRoot) {
    $path=Join-Path $ModuleRoot 'common\bufio\copy_direct_windows.go'
    $text=[IO.File]::ReadAllText($path).Replace("`r`n","`n")
    $anchor="`terr = w.rawConn.Read(w.readFunc)"
    if(([regex]::Matches($text,[regex]::Escape($anchor))).Count -ne 3){throw 'Pinned Windows socket read patch context differs.'}
    $replacement="`t// A timed-out readiness wait must not reuse the previous hasData flag.`n`tw.hasData = false`n"+$anchor
    [IO.File]::WriteAllText($path,$text.Replace($anchor,$replacement),[Text.UTF8Encoding]::new($false))
}
