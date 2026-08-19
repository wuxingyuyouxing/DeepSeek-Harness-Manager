@SETLOCAL
@IF EXIST "%~dp0\node.exe" (
  "%~dp0\node.exe"  "%~dp0\..\..\..\PY\web-2\nodejs\node-v24.18.1-win-x64\node_modules\corepack\dist\yarnpkg.js" %*
) ELSE (
  @SET PATHEXT=%PATHEXT:;.JS;=;%
  node  "%~dp0\..\..\..\PY\web-2\nodejs\node-v24.18.1-win-x64\node_modules\corepack\dist\yarnpkg.js" %*
)
