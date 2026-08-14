' start-dsh.vbs - DeepSeek Harness one-click launcher (hidden window runner)
' Double-click this file (or the desktop shortcut) to start the service and
' open the browser, with no console window.
Option Explicit
Dim fso, shell, root, ps
Set fso   = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fso.GetParentFolderName(WScript.ScriptFullName)
ps   = """" & root & "\start-dsh.ps1"""
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " & ps, 0, False
