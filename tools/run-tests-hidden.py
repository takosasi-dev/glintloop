"""テストのプログラムを Windows の隠れたデスクトップで動かす(利用者の画面に窓を出さない。マウスも届かない)。

先に `dotnet build GlintLoop.slnx -c Release` する。使い方: GLINT_ROOT=<リポジトリ> python run-tests-hidden.py <出力ファイルの絶対パス> --all [--filter 語]
"""
import ctypes
import os
import ctypes.wintypes as w
import pathlib
import sys

ROOT = pathlib.Path(os.environ.get("GLINT_ROOT", pathlib.Path(__file__).resolve().parent.parent))
EXE = ROOT / r"tests\GlintLoop.Tests\bin\Release\net10.0-windows\GlintLoop.Tests.exe"
out = sys.argv[1]
args = " ".join('"%s"' % a if " " in a else a for a in sys.argv[2:])

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32.CreateDesktopW.restype = w.HANDLE
user32.CreateDesktopW.argtypes = [w.LPCWSTR, w.LPCWSTR, ctypes.c_void_p, w.DWORD, w.DWORD, ctypes.c_void_p]
NAME = f"GlintLoopTestsHidden{os.getpid()}"
desk = user32.CreateDesktopW(NAME, None, None, 0, 0x10000000, None)
if not desk:
    raise ctypes.WinError(ctypes.get_last_error())


class SI(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("lpReserved", w.LPWSTR), ("lpDesktop", w.LPWSTR), ("lpTitle", w.LPWSTR),
                ("x", w.DWORD), ("y", w.DWORD), ("xs", w.DWORD), ("ys", w.DWORD), ("xc", w.DWORD), ("yc", w.DWORD),
                ("attr", w.DWORD), ("flags", w.DWORD), ("show", w.WORD), ("r2", w.WORD), ("r3", ctypes.c_void_p),
                ("hin", w.HANDLE), ("hout", w.HANDLE), ("herr", w.HANDLE)]


class PI(ctypes.Structure):
    _fields_ = [("hProcess", w.HANDLE), ("hThread", w.HANDLE), ("pid", w.DWORD), ("tid", w.DWORD)]


si = SI()
si.cb = ctypes.sizeof(SI)
si.lpDesktop = NAME
pi = PI()
cmd = ctypes.create_unicode_buffer(f'cmd.exe /c ""{EXE}" {args} > "{out}" 2>&1"')
kernel32.CreateProcessW.argtypes = [w.LPCWSTR, w.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, w.BOOL, w.DWORD, ctypes.c_void_p, w.LPCWSTR, ctypes.POINTER(SI), ctypes.POINTER(PI)]
ok = kernel32.CreateProcessW(None, cmd, None, None, False, 0x08000000, None, str(ROOT), ctypes.byref(si), ctypes.byref(pi))
if not ok:
    raise ctypes.WinError(ctypes.get_last_error())
kernel32.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
kernel32.WaitForSingleObject(pi.hProcess, 0xFFFFFFFF)
code = w.DWORD()
kernel32.GetExitCodeProcess(pi.hProcess, ctypes.byref(code))
print("exit", code.value)
user32.CloseDesktop.argtypes = [w.HANDLE]
user32.CloseDesktop(desk)
