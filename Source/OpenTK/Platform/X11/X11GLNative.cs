﻿#region --- License ---
/* Copyright (c) 2007 Stefanos Apostolopoulos
 * See license.txt for license info
 */
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Reflection;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using OpenTK.Platform.Windows;
using OpenTK.Graphics;

//using OpenTK.Graphics.OpenGL;

namespace OpenTK.Platform.X11
{
    /// <summary>
    /// Drives GameWindow on X11.
    /// This class supports OpenTK, and is not intended for use by OpenTK programs.
    /// </summary>
    internal sealed class X11GLNative : INativeGLWindow, IDisposable
    {
        // TODO: Disable screensaver.
        // TODO: What happens if we can't disable decorations through motif?
        // TODO: Mouse/keyboard grabbing/wrapping.
        // TODO: PointToWindow, PointToScreen

        #region --- Fields ---

        X11WindowInfo window = new X11WindowInfo();
        X11Input driver;

        // Window manager hints for fullscreen windows.
        const string MOTIF_WM_ATOM = "_MOTIF_WM_HINTS";
        const string KDE_WM_ATOM = "KWM_WIN_DECORATION";
        const string KDE_NET_WM_ATOM = "_KDE_NET_WM_WINDOW_TYPE";
        const string ICCM_WM_ATOM = "_NET_WM_WINDOW_TYPE";

        // Number of pending events.
        int pending = 0;

        int width, height;
        int top, bottom, left, right;

        // C# ResizeEventArgs
        ResizeEventArgs resizeEventArgs = new ResizeEventArgs();

        // Used for event loop.
        XEvent e = new XEvent();

        bool disposed;
        bool exists;
        bool isExiting;

        // XAtoms for window properties
        static IntPtr WMTitle;      // The title of the GameWindow.
        static IntPtr UTF8String;   // No idea.

        // Fields used for fullscreen mode changes.
        int pre_fullscreen_width, pre_fullscreen_height;
        bool fullscreen = false;

        #endregion

        #region --- Constructors ---

        /// <summary>
        /// Constructs and initializes a new X11GLNative window.
        /// Call CreateWindow to create the actual render window.
        /// </summary>
        public X11GLNative()
        {
            try
            {
                Debug.Print("Creating X11GLNative window.");
                Debug.Indent();

                //Utilities.ThrowOnX11Error = true; // Not very reliable

                // Open the display to the X server, and obtain the screen and root window.
                window.Display = API.OpenDisplay(null); // null == default display //window.Display = API.DefaultDisplay;
                if (window.Display == IntPtr.Zero)
                    throw new Exception("Could not open connection to X");

                window.Screen = Functions.XDefaultScreen(window.Display); //API.DefaultScreen;
                window.RootWindow = Functions.XRootWindow(window.Display, window.Screen); // API.RootWindow;
                Debug.Print("Display: {0}, Screen {1}, Root window: {2}", window.Display, window.Screen, window.RootWindow);

                RegisterAtoms(window);
            }
            finally
            {
                Debug.Unindent();
            }
        }

        #endregion

        #region private static void RegisterAtoms()

        /// <summary>
        /// Not used yet.
        /// Registers the necessary atoms for GameWindow.
        /// </summary>
        private static void RegisterAtoms(X11WindowInfo window)
        {
            string[] atom_names = new string[] 
            {
                "WM_TITLE",
                "UTF8_STRING"
            };
            IntPtr[] atoms = new IntPtr[atom_names.Length];
            //Functions.XInternAtoms(window.Display, atom_names, atom_names.Length, false, atoms);

            int offset = 0;
            WMTitle = atoms[offset++];
            UTF8String = atoms[offset++];
        }

        #endregion

        #region --- INativeGLWindow Members ---

        #region public void CreateWindow(int width, int height, GraphicsMode mode, out IGraphicsContext context)

        public void CreateWindow(int width, int height, GraphicsMode mode, out IGraphicsContext context)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException("width", "Must be higher than zero.");
            if (height <= 0) throw new ArgumentOutOfRangeException("height", "Must be higher than zero.");
            if (exists) throw new InvalidOperationException("A render window already exists.");

            Debug.Indent();

            // Create the context. This call also creates an XVisualInfo structure for us.
            context = new GraphicsContext(mode, window);

            // Create a window on this display using the visual above
            Debug.Write("Opening render window... ");

            XSetWindowAttributes attributes = new XSetWindowAttributes();
            attributes.background_pixel = IntPtr.Zero;
            attributes.border_pixel = IntPtr.Zero;
            attributes.colormap =
                API.CreateColormap(window.Display, window.RootWindow, window.VisualInfo.visual, 0/*AllocNone*/);
            window.EventMask =
                EventMask.StructureNotifyMask | EventMask.SubstructureNotifyMask | EventMask.ExposureMask |
                EventMask.KeyReleaseMask | EventMask.KeyPressMask |
                    EventMask.PointerMotionMask | // Bad! EventMask.PointerMotionHintMask |
                    EventMask.ButtonPressMask | EventMask.ButtonReleaseMask;
            attributes.event_mask = (IntPtr)window.EventMask;

            uint mask = (uint)SetWindowValuemask.ColorMap | (uint)SetWindowValuemask.EventMask |
                (uint)SetWindowValuemask.BackPixel | (uint)SetWindowValuemask.BorderPixel;

            window.Handle = Functions.XCreateWindow(window.Display, window.RootWindow,
                0, 0, width, height, 0, window.VisualInfo.depth/*(int)CreateWindowArgs.CopyFromParent*/,
                (int)CreateWindowArgs.InputOutput, window.VisualInfo.visual, (UIntPtr)mask, ref attributes);

            if (window.Handle == IntPtr.Zero)
                throw new ApplicationException("XCreateWindow call failed (returned 0).");

            XVisualInfo vis = window.VisualInfo;
            Glx.CreateContext(window.Display, ref vis, IntPtr.Zero, true);

            // Set the window hints
            XSizeHints hints = new XSizeHints();
            hints.x = 0;
            hints.y = 0;
            hints.width = width;
            hints.height = height;
            hints.flags = (IntPtr)(XSizeHintsFlags.USSize | XSizeHintsFlags.USPosition);
            Functions.XSetWMNormalHints(window.Display, window.Handle, ref hints);

            // Register for window destroy notification
            IntPtr wm_destroy_atom = Functions.XInternAtom(window.Display, "WM_DELETE_WINDOW", true);
            //XWMHints hint = new XWMHints();
            Functions.XSetWMProtocols(window.Display, window.Handle, new IntPtr[] { wm_destroy_atom }, 1);

            Top = Left = 0;
            Right = Width;
            Bottom = Height;

            //XTextProperty text = new XTextProperty();
            //text.value = "OpenTK Game Window";
            //text.format = 8;
            //Functions.XSetWMName(window.Display, window.Handle, ref text);
            //Functions.XSetWMProperties(display, window, name, name, 0,  /*None*/ null, 0, hints);

            Debug.Print("done! (id: {0})", window.Handle);

            //(glContext as IGLContextCreationHack).SetWindowHandle(window.Handle);

            API.MapRaised(window.Display, window.Handle);
            mapped = true;

            //context.CreateContext(true, null);

            driver = new X11Input(window);

            Debug.WriteLine("X11GLNative window created successfully!");
            Debug.Unindent();
            exists = true;
        }

        #endregion

        #region public void ProcessEvents()

        public void ProcessEvents()
        {
            // Process all pending events
            while (true)
            {
                //pending = Functions.XPending(window.Display);
                pending = API.Pending(window.Display);

                if (pending == 0)
                    return;

                Functions.XNextEvent(window.Display, ref e);

                //Debug.Print("Event: {0} ({1} pending)", e.type, pending);

                // Respond to the event e
                switch (e.type)
                {
                    case XEventName.MapNotify:
                        Debug.WriteLine("Window mapped.");
                        return;

                    case XEventName.CreateNotify:
                        // A child was was created - nothing to do
                        break;

                    case XEventName.ClientMessage:
                        this.OnDestroy(EventArgs.Empty);
                        break;

                    case XEventName.DestroyNotify:
                        exists = false;
                        isExiting = true;
                        Debug.Print("X11 window {0} destroyed.", e.DestroyWindowEvent.window);
                        return;

                    case XEventName.ConfigureNotify:
                        // If the window size changed, raise the C# Resize event.
                        if (e.ConfigureEvent.width != width || e.ConfigureEvent.height != height)
                        {
                            Debug.WriteLine(String.Format("ConfigureNotify: {0}x{1}", e.ConfigureEvent.width, e.ConfigureEvent.height));

                            resizeEventArgs.Width = e.ConfigureEvent.width;
                            resizeEventArgs.Height = e.ConfigureEvent.height;
                            this.OnResize(resizeEventArgs);
                        }
                        break;

                    case XEventName.KeyPress:
                    case XEventName.KeyRelease:
                    case XEventName.MotionNotify:
                    case XEventName.ButtonPress:
                    case XEventName.ButtonRelease:
                        //Functions.XPutBackEvent(window.Display, ref e);
                        driver.ProcessEvent(ref e);
                        break;

                    default:
                        Debug.WriteLine(String.Format("{0} event was not handled", e.type));
                        break;
                }
            }
        }

        #endregion

        #region public IInputDriver InputDriver

        public IInputDriver InputDriver
        {
            get
            {
                return driver;
            }
        }

        #endregion 

        #region public bool Exists

        /// <summary>
        /// Returns true if a render window/context exists.
        /// </summary>
        public bool Exists
        {
            get { return exists; }
        }

        #endregion

        #region public bool Quit

        public bool IsExiting
        {
            get { return isExiting; }
        }

        #endregion

        #region public bool IsIdle

        public bool IsIdle
        {
            get { throw new Exception("The method or operation is not implemented."); }
        }

        #endregion

        #region public bool Fullscreen

        public bool Fullscreen
        {
            get
            {
                return fullscreen;
            }
            set
            {
                if (value && !fullscreen)
                {
                    Debug.Print("Going fullscreen");
                    Debug.Indent();
                    DisableWindowDecorations();
                    pre_fullscreen_height = this.Height;
                    pre_fullscreen_width = this.Width;
                    //Functions.XRaiseWindow(this.window.Display, this.Handle);
                    Functions.XMoveResizeWindow(this.window.Display, this.Handle, 0, 0, 
                        DisplayDevice.Default.Width, DisplayDevice.Default.Height);
                    Debug.Unindent();
                    fullscreen = true;
                }
                else if (!value && fullscreen)
                {
                    Debug.Print("Going windowed");
                    Debug.Indent();
                    Functions.XMoveResizeWindow(this.window.Display, this.Handle, 0, 0,
                        pre_fullscreen_width, pre_fullscreen_height);
                    pre_fullscreen_height = pre_fullscreen_width = 0;
                    EnableWindowDecorations();
                    Debug.Unindent();
                    fullscreen = false;
                }
                /*
                Debug.Print(value ? "Going fullscreen" : "Going windowed");
                IntPtr state_atom = Functions.XInternAtom(this.window.Display, "_NET_WM_STATE", false);
                IntPtr fullscreen_atom = Functions.XInternAtom(this.window.Display, "_NET_WM_STATE_FULLSCREEN", false);
                XEvent xev = new XEvent();
                xev.ClientMessageEvent.type = XEventName.ClientMessage;
                xev.ClientMessageEvent.serial = IntPtr.Zero;
                xev.ClientMessageEvent.send_event = true;
                xev.ClientMessageEvent.window = this.Handle;
                xev.ClientMessageEvent.message_type = state_atom;
                xev.ClientMessageEvent.format = 32;
                xev.ClientMessageEvent.ptr1 = (IntPtr)(value ? NetWindowManagerState.Add : NetWindowManagerState.Remove);
                xev.ClientMessageEvent.ptr2 = (IntPtr)(value ? 1 : 0);
                xev.ClientMessageEvent.ptr3 = IntPtr.Zero;
                Functions.XSendEvent(this.window.Display, API.RootWindow, false,
                    (IntPtr)(EventMask.SubstructureRedirectMask | EventMask.SubstructureNotifyMask), ref xev);

                fullscreen = !fullscreen;
                */
            }
        }

        #endregion

        #region public IntPtr Handle

        /// <summary>
        /// Gets the current window handle.
        /// </summary>
        public IntPtr Handle
        {
            get { return this.window.Handle; }
        }

        #endregion

        #region public string Title

        /// <summary>
        /// TODO: Use atoms for this property.
        /// Gets or sets the GameWindow title.
        /// </summary>
        public string Title
        {
            get
            {
                IntPtr name = IntPtr.Zero;
                Functions.XFetchName(window.Display, window.Handle, ref name);
                if (name != IntPtr.Zero)
                    return Marshal.PtrToStringAnsi(name);

                return String.Empty;
            }
            set
            {
                /*
                XTextProperty name = new XTextProperty();
                name.format = 8; //STRING
                if (value == null)
                    name.value = String.Empty;
                else
                    name.value = value;

                Functions.XSetWMName(window.Display, window.Handle, ref name);
                */
                if (value != null)
                    Functions.XStoreName(window.Display, window.Handle, value);
            }
        }

        #endregion

        #region public bool Visible

        bool mapped;
        public bool Visible
        {
            get
            {
                //return true;
                return mapped;
            }
            set
            {
                if (value && !mapped)
                {
                    Functions.XMapWindow(window.Display, window.Handle);
                    mapped = true;
                }
                else if (!value && mapped)
                {
                    Functions.XUnmapWindow(window.Display, window.Handle);
                    mapped = false;
                }
            }
        }

        #endregion

        #region public IWindowInfo WindowInfo

        public IWindowInfo WindowInfo
        {
            get { return window; }
        }

        #endregion

        #region OnCreate

        public event CreateEvent Create;

        private void OnCreate(EventArgs e)
        {
            if (this.Create != null)
            {
                Debug.Print("Create event fired from window: {0}", window.ToString());
                this.Create(this, e);
            }
        }

        #endregion

        #region public void Exit()

        public void Exit()
        {
            this.DestroyWindow();
        }

        #endregion

        #region public void DestroyWindow()

        public void DestroyWindow()
        {
            Debug.WriteLine("X11GLNative shutdown sequence initiated.");
            Functions.XDestroyWindow(window.Display, window.Handle);
        }

        #endregion

        #region OnDestroy

        public event DestroyEvent Destroy;

        private void OnDestroy(EventArgs e)
        {
            Debug.Print("Destroy event fired from window: {0}", window.ToString());
            if (this.Destroy != null)
                this.Destroy(this, e);
        }

        #endregion

        #region PointToClient

        public void PointToClient(ref System.Drawing.Point p)
        {
            /*
            if (!Functions.ScreenToClient(this.Handle, p))
                throw new InvalidOperationException(String.Format(
                    "Could not convert point {0} from client to screen coordinates. Windows error: {1}",
                    p.ToString(), Marshal.GetLastWin32Error()));
            */
        }

        #endregion

        #region PointToScreen

        public void PointToScreen(ref System.Drawing.Point p)
        {
            throw new NotImplementedException();
        }

        #endregion

        #endregion

        #region --- IResizable Members ---

        #region public int Width

        public int Width
        {
            get
            {
                return width;
            }
            set
            {/*
                // Clear event struct
                //Array.Clear(xresize.pad, 0, xresize.pad.Length);
                // Set requested parameters
                xresize.ResizeRequest.type = EventType.ResizeRequest;
                xresize.ResizeRequest.display = this.display;
                xresize.ResizeRequest.width = value;
                xresize.ResizeRequest.height = mode.Width;
                API.SendEvent(
                    this.display,
                    this.window,
                    false,
                    EventMask.StructureNotifyMask,
                    ref xresize
                );*/
            }
        }

        #endregion

        #region public int Height

        public int Height
        {
            get
            {
                return height;
            }
            set
            {/*
                // Clear event struct
                //Array.Clear(xresize.pad, 0, xresize.pad.Length);
                // Set requested parameters
                xresize.ResizeRequest.type = EventType.ResizeRequest;
                xresize.ResizeRequest.display = this.display;
                xresize.ResizeRequest.width = mode.Width;
                xresize.ResizeRequest.height = value;
                API.SendEvent(
                    this.display,
                    this.window,
                    false,
                    EventMask.StructureNotifyMask,
                    ref xresize
                );*/
            }
        }

        #endregion

        #region public event ResizeEvent Resize

        public event ResizeEvent Resize;

        private void OnResize(ResizeEventArgs e)
        {
            width = e.Width;
            height = e.Height;
            if (this.Resize != null)
            {
                this.Resize(this, e);
            }
        }

        #endregion

        public int Top
        {
            get { return top; }
            private set { top = value; }
        }

        public int Bottom
        {
            get { return bottom; }
            private set { bottom = value; }
        }

        public int Left
        {
            get { return left; }
            private set { left = value; }
        }

        public int Right
        {
            get { return right; }
            private set { right = value; }
        }

        #endregion

        #region --- IDisposable Members ---

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool manuallyCalled)
        {
            if (!disposed)
            {
                if (manuallyCalled)
                {
                    //if (glContext != null)
                    //    glContext.Dispose();

                    // Kills connection to the X-Server. We don't want that,
                    // 'cause it kills the ExampleLauncher too.
                    //Functions.XCloseDisplay(window.Display);
                }

                Functions.XUnmapWindow(window.Display, window.Handle);
                Functions.XDestroyWindow(window.Display, window.Handle);

                disposed = true;
            }
        }

        ~X11GLNative()
        {
            this.Dispose(false);
        }

        #endregion

        #region --- Private Methods ---

        #region void DisableWindowDecorations()

        void DisableWindowDecorations()
        {
            bool removed = false;
            if (DisableMotifDecorations()) { Debug.Print("Removed decorations through motif."); removed = true; }
            if (DisableGnomeDecorations()) { Debug.Print("Removed decorations through gnome."); removed = true; }
            if (DisableIccmDecorations()) { Debug.Print("Removed decorations through ICCM."); removed = true; }

            if (removed)
            {
                Functions.XSetTransientForHint(this.window.Display, this.Handle, this.window.RootWindow);
                Functions.XUnmapWindow(this.window.Display, this.Handle);
                Functions.XMapWindow(this.window.Display, this.Handle);
            }
        }

        #region bool DisableMotifDecorations()

        bool DisableMotifDecorations()
        {
            IntPtr atom = Functions.XInternAtom(this.window.Display, MOTIF_WM_ATOM, true);
            if (atom != IntPtr.Zero)
            {
                MotifWmHints hints = new MotifWmHints();
                hints.flags = (IntPtr)MotifFlags.Decorations;
                Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace, ref hints, 5
                    /*Marshal.SizeOf(hints) / 4*/);
                return true;
            }
            return false;
        }

        #endregion

        #region bool DisableGnomeDecorations()

        bool DisableGnomeDecorations()
        {
            // Attempt to cover gnome panels.
            //XEvent xev = new XEvent();
            //xev.ClientMessageEvent.window = this.window.Handle;
            //xev.ClientMessageEvent.type = XEventName.ClientMessage;
            //xev.ClientMessageEvent.message_type = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_LAYER, false);
            //xev.ClientMessageEvent.format = 32;
            //xev.ClientMessageEvent.ptr1 = (IntPtr)WindowLayer.AboveDock;
            //Functions.XSendEvent(this.window.Display, this.window.RootWindow, false, (IntPtr)EventMask.SubstructureNotifyMask, ref xev);

            //xev = new XEvent();
            //xev.ClientMessageEvent.window = this.window.Handle;
            //xev.ClientMessageEvent.type = XEventName.ClientMessage;
            //xev.ClientMessageEvent.message_type = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_STATE, false);
            //xev.ClientMessageEvent.format = 32;
            //xev.ClientMessageEvent.ptr1 = (IntPtr)WindowState.;
            //xev.ClientMessageEvent.ptr2 = (IntPtr)WindowLayer.AboveDock;
            //Functions.XSendEvent(this.window.Display, this.window.RootWindow, false, (IntPtr)EventMask.SubstructureNotifyMask, ref xev);
            
            IntPtr atom = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_HINTS, true);
            if (atom != IntPtr.Zero)
            {
                IntPtr hints = IntPtr.Zero;
                Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace, ref hints,
                    /*Marshal.SizeOf(hints) / 4*/ 1);
                return true;
            }

            return false;
        }

        #endregion

        #region bool DisableIccmDecorations()

        bool DisableIccmDecorations()
        {
            IntPtr atom = Functions.XInternAtom(this.window.Display, ICCM_WM_ATOM, true);
            if (atom != IntPtr.Zero)
            {
                IntPtr hints = Functions.XInternAtom(this.window.Display, "_NET_WM_STATE_FULLSCREEN", true); 
                Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace, ref hints, 1
                    /*Marshal.SizeOf(hints) / 4*/);
                return true;
            }
            return false;
        }

        #endregion

        #endregion

        #region void EnableWindowDecorations()

        void EnableWindowDecorations()
        {
            bool activated = false;
            if (EnableMotifDecorations()) { Debug.Print("Activated decorations through motif."); activated = true; }
            if (EnableGnomeDecorations()) { Debug.Print("Activated decorations through gnome."); activated = true; }
            if (EnableIccmDecorations()) { Debug.Print("Activated decorations through ICCM."); activated = true; }

            if (activated)
            {
                Functions.XSetTransientForHint(this.window.Display, this.Handle, this.window.RootWindow);
                Functions.XUnmapWindow(this.window.Display, this.Handle);
                Functions.XMapWindow(this.window.Display, this.Handle);
            }
        }

        #region bool EnableMotifDecorations()

        bool EnableMotifDecorations()
        {
            IntPtr atom = Functions.XInternAtom(this.window.Display, MOTIF_WM_ATOM, true);
            if (atom != IntPtr.Zero)
            {
                Functions.XDeleteProperty(this.window.Display, this.Handle, atom);
                return true;
            }
            return false;
        }

        #endregion

        #region bool EnableGnomeDecorations()

        bool EnableGnomeDecorations()
        {
            // Restore window layer.
            //XEvent xev = new XEvent();
            //xev.ClientMessageEvent.window = this.window.Handle;
            //xev.ClientMessageEvent.type = XEventName.ClientMessage;
            //xev.ClientMessageEvent.message_type = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_LAYER, false);
            //xev.ClientMessageEvent.format = 32;
            //xev.ClientMessageEvent.ptr1 = (IntPtr)WindowLayer.AboveDock;
            //Functions.XSendEvent(this.window.Display, this.window.RootWindow, false, (IntPtr)EventMask.SubstructureNotifyMask, ref xev);
            
            IntPtr atom = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_HINTS, true);
            if (atom != IntPtr.Zero)
            {
                Functions.XDeleteProperty(this.window.Display, this.Handle, atom);
                return true;
            }

            return false;
        }

        #endregion

        #region bool EnableIccmDecorations()

        bool EnableIccmDecorations()
        {
            IntPtr atom = Functions.XInternAtom(this.window.Display, ICCM_WM_ATOM, true);
            if (atom != IntPtr.Zero)
            {
                IntPtr hint = Functions.XInternAtom(this.window.Display, "_NET_WM_WINDOW_TYPE_NORMAL", true);
                if (hint != IntPtr.Zero)
                {
                    Functions.XChangeProperty(this.window.Display, this.Handle, hint, /*XA_ATOM*/(IntPtr)4, 32, PropertyMode.Replace,
                        ref hint, Marshal.SizeOf(hint) / 4);
                }
                return true;
            }
            return false;
        }

        #endregion

        #endregion

        #endregion
    }
}
