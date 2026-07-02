$n = [System.Text.Encoding]::ASCII.GetString([byte[]](82,101,112,111,114,116,105,110,103,68,97,115,104,98,111,97,114,100))
$p = "C:\Git\VirtualDevTeam\src\$n"
[IO.File]::WriteAllText("$p\test.txt", "hello from script")