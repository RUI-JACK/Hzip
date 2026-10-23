import ctypes, sys, pathlib
from ctypes import wintypes
from PIL import Image
u=ctypes.windll.user32;g=ctypes.windll.gdi32
try: u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except AttributeError: pass
u.GetWindowDC.argtypes=[wintypes.HWND];u.GetWindowDC.restype=wintypes.HDC
g.CreateCompatibleDC.argtypes=[wintypes.HDC];g.CreateCompatibleDC.restype=wintypes.HDC
g.CreateCompatibleBitmap.argtypes=[wintypes.HDC,ctypes.c_int,ctypes.c_int];g.CreateCompatibleBitmap.restype=wintypes.HBITMAP
g.SelectObject.argtypes=[wintypes.HDC,wintypes.HGDIOBJ];g.SelectObject.restype=wintypes.HGDIOBJ
u.PrintWindow.argtypes=[wintypes.HWND,wintypes.HDC,wintypes.UINT]
class BMIH(ctypes.Structure):
 _fields_=[('biSize',wintypes.DWORD),('biWidth',wintypes.LONG),('biHeight',wintypes.LONG),('biPlanes',wintypes.WORD),('biBitCount',wintypes.WORD),('biCompression',wintypes.DWORD),('biSizeImage',wintypes.DWORD),('biXPelsPerMeter',wintypes.LONG),('biYPelsPerMeter',wintypes.LONG),('biClrUsed',wintypes.DWORD),('biClrImportant',wintypes.DWORD)]
hwnd=int(sys.argv[1]);out=pathlib.Path(sys.argv[2]);r=wintypes.RECT();u.GetWindowRect(hwnd,ctypes.byref(r));w=r.right-r.left;h=r.bottom-r.top
dc=u.GetWindowDC(hwnd);mem=g.CreateCompatibleDC(dc);bmp=g.CreateCompatibleBitmap(dc,w,h);old=g.SelectObject(mem,bmp)
try:
 ok=u.PrintWindow(hwnd,mem,2)
 info=BMIH();info.biSize=ctypes.sizeof(info);info.biWidth=w;info.biHeight=-h;info.biPlanes=1;info.biBitCount=32
 data=ctypes.create_string_buffer(w*h*4)
 g.GetDIBits.argtypes=[wintypes.HDC,wintypes.HBITMAP,wintypes.UINT,wintypes.UINT,ctypes.c_void_p,ctypes.c_void_p,wintypes.UINT]
 lines=g.GetDIBits(mem,bmp,0,h,data,ctypes.byref(info),0)
 image=Image.frombuffer('RGB',(w,h),data,'raw','BGRX',0,1);out.parent.mkdir(parents=True,exist_ok=True);image.save(out)
 print('PrintWindow',ok,'lines',lines,'range',image.getextrema())
finally:
 g.SelectObject(mem,old);g.DeleteObject.argtypes=[wintypes.HGDIOBJ];g.DeleteObject(bmp);g.DeleteDC.argtypes=[wintypes.HDC];g.DeleteDC(mem);u.ReleaseDC.argtypes=[wintypes.HWND,wintypes.HDC];u.ReleaseDC(hwnd,dc)
