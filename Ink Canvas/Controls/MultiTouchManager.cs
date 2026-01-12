using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Input.StylusPlugIns;

namespace Ink_Canvas.Controls
{
    public class MultiTouchManager
    {
        private Dictionary<int, TouchDevice> _activeTouches = new Dictionary<int, TouchDevice>();
        private Dictionary<int, Point> _touchStartPositions = new Dictionary<int, Point>();
        private Dictionary<int, Point> _touchCurrentPositions = new Dictionary<int, Point>();
        private Dictionary<int, Point> _previousPositions = new Dictionary<int, Point>();
        private bool _isMultiTouchEnabled = false;
        
        public event EventHandler<MultiTouchEventArgs> MultiTouchStarted;
        public event EventHandler<MultiTouchEventArgs> MultiTouchDelta;
        public event EventHandler<MultiTouchEventArgs> MultiTouchCompleted;
        public event EventHandler<MultiTouchGestureEventArgs> MultiTouchGestureDetected;

        public bool IsMultiTouchEnabled
        {
            get { return _isMultiTouchEnabled; }
            set { _isMultiTouchEnabled = value; }
        }

        public void ProcessTouchDown(FrameworkElement element, TouchEventArgs e)
        {
            if (!_isMultiTouchEnabled) return;

            int touchId = e.TouchDevice.Id;
            
            if (!_activeTouches.ContainsKey(touchId))
            {
                _activeTouches[touchId] = e.TouchDevice;
                Point touchPoint = e.GetTouchPoint(element).Position;
                _touchStartPositions[touchId] = touchPoint;
                _touchCurrentPositions[touchId] = touchPoint;
                _previousPositions[touchId] = touchPoint;
                
                // Capture the touch device
                element.CaptureTouch(e.TouchDevice);
                
                // Check if we now have multiple touches active
                if (_activeTouches.Count >= 2)
                {
                    OnMultiTouchStarted(new MultiTouchEventArgs(element, _activeTouches.Values.ToList()));
                }
            }
        }

        public void ProcessTouchMove(FrameworkElement element, TouchEventArgs e)
        {
            if (!_isMultiTouchEnabled) return;

            int touchId = e.TouchDevice.Id;
            
            if (_activeTouches.ContainsKey(touchId))
            {
                Point touchPoint = e.GetTouchPoint(element).Position;
                _previousPositions[touchId] = _touchCurrentPositions[touchId]; // Store previous position
                _touchCurrentPositions[touchId] = touchPoint;
                
                // Check if we have multiple touches active
                if (_activeTouches.Count >= 2)
                {
                    var args = new MultiTouchEventArgs(element, _activeTouches.Values.ToList(), _touchCurrentPositions);
                    
                    // Detect gestures
                    DetectGestures(element, args);
                    
                    OnMultiTouchDelta(args);
                }
            }
        }

        public void ProcessTouchUp(FrameworkElement element, TouchEventArgs e)
        {
            if (!_isMultiTouchEnabled) return;

            int touchId = e.TouchDevice.Id;
            
            if (_activeTouches.ContainsKey(touchId))
            {
                Point touchPoint = e.GetTouchPoint(element).Position;
                _touchCurrentPositions[touchId] = touchPoint;
                
                // Check if we had multiple touches active
                bool wasMultiTouch = _activeTouches.Count >= 2;
                
                // Remove the touch
                _activeTouches.Remove(touchId);
                _touchStartPositions.Remove(touchId);
                _touchCurrentPositions.Remove(touchId);
                _previousPositions.Remove(touchId);
                
                // Release touch capture
                element.ReleaseTouchCapture(e.TouchDevice);
                
                if (wasMultiTouch)
                {
                    // We might still have multi-touch if other fingers remain
                    if (_activeTouches.Count >= 2)
                    {
                        OnMultiTouchDelta(new MultiTouchEventArgs(element, _activeTouches.Values.ToList(), _touchCurrentPositions));
                    }
                    else
                    {
                        // Multi-touch ended
                        OnMultiTouchCompleted(new MultiTouchEventArgs(element, _activeTouches.Values.ToList()));
                    }
                }
            }
        }

        private void DetectGestures(FrameworkElement element, MultiTouchEventArgs e)
        {
            if (e.CurrentPositions.Count < 2) return;

            // Calculate gesture properties
            var positions = e.CurrentPositions.Values.ToList();
            var prevPositions = _previousPositions.Values.ToList();
            
            if (positions.Count >= 2 && prevPositions.Count >= 2)
            {
                // Calculate current and previous distances between first two touches
                var currPosList = positions.Take(2).ToList();
                var prevPosList = prevPositions.Take(2).ToList();
                
                double currentDistance = CalculateDistance(currPosList[0], currPosList[1]);
                double previousDistance = CalculateDistance(prevPosList[0], prevPosList[1]);
                
                // Calculate movement of center point
                Point currentCenter = CalculateMidPoint(currPosList[0], currPosList[1]);
                Point previousCenter = CalculateMidPoint(prevPosList[0], prevPosList[1]);
                
                double scaleChange = previousDistance > 0 ? currentDistance / previousDistance : 1.0;
                Vector translation = new Vector(
                    currentCenter.X - previousCenter.X,
                    currentCenter.Y - previousCenter.Y
                );
                
                // Determine gesture type
                MultiTouchGestureType gestureType = MultiTouchGestureType.Unknown;
                
                if (Math.Abs(scaleChange - 1.0) > 0.1) // Significant scaling detected
                {
                    gestureType = scaleChange > 1.0 ? MultiTouchGestureType.ZoomIn : MultiTouchGestureType.ZoomOut;
                }
                else if (translation.Length > 5) // Significant translation detected
                {
                    gestureType = MultiTouchGestureType.Pan;
                }
                
                if (gestureType != MultiTouchGestureType.Unknown)
                {
                    var gestureArgs = new MultiTouchGestureEventArgs(element, gestureType, scaleChange, translation, currentCenter);
                    OnMultiTouchGestureDetected(gestureArgs);
                }
            }
        }

        private double CalculateDistance(Point p1, Point p2)
        {
            return Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
        }

        private Point CalculateMidPoint(Point p1, Point p2)
        {
            return new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
        }

        protected virtual void OnMultiTouchStarted(MultiTouchEventArgs e)
        {
            MultiTouchStarted?.Invoke(this, e);
        }

        protected virtual void OnMultiTouchDelta(MultiTouchEventArgs e)
        {
            MultiTouchDelta?.Invoke(this, e);
        }

        protected virtual void OnMultiTouchCompleted(MultiTouchEventArgs e)
        {
            MultiTouchCompleted?.Invoke(this, e);
        }

        protected virtual void OnMultiTouchGestureDetected(MultiTouchGestureEventArgs e)
        {
            MultiTouchGestureDetected?.Invoke(this, e);
        }

        public void ClearAllTouches()
        {
            _activeTouches.Clear();
            _touchStartPositions.Clear();
            _touchCurrentPositions.Clear();
            _previousPositions.Clear();
        }

        public int GetActiveTouchCount()
        {
            return _activeTouches.Count;
        }

        public bool HasMultiTouchActive()
        {
            return _activeTouches.Count >= 2;
        }
    }

    public enum MultiTouchGestureType
    {
        Unknown,
        ZoomIn,
        ZoomOut,
        Pan,
        Rotate
    }

    public class MultiTouchGestureEventArgs : EventArgs
    {
        public FrameworkElement Element { get; private set; }
        public MultiTouchGestureType GestureType { get; private set; }
        public double ScaleChange { get; private set; }
        public Vector Translation { get; private set; }
        public Point CenterPoint { get; private set; }

        public MultiTouchGestureEventArgs(FrameworkElement element, MultiTouchGestureType gestureType, 
            double scaleChange, Vector translation, Point centerPoint)
        {
            Element = element;
            GestureType = gestureType;
            ScaleChange = scaleChange;
            Translation = translation;
            CenterPoint = centerPoint;
        }
    }

    public class MultiTouchEventArgs : EventArgs
    {
        public List<TouchDevice> ActiveTouches { get; private set; }
        public FrameworkElement Element { get; private set; }
        public Dictionary<int, Point> CurrentPositions { get; private set; }

        public MultiTouchEventArgs(FrameworkElement element, List<TouchDevice> activeTouches, Dictionary<int, Point> currentPositions = null)
        {
            Element = element;
            ActiveTouches = activeTouches;
            CurrentPositions = currentPositions ?? new Dictionary<int, Point>();
        }

        public double CalculateDistanceBetweenFirstTwoTouches()
        {
            if (ActiveTouches.Count < 2 || CurrentPositions.Count < 2) return 0;

            var touchIds = CurrentPositions.Keys.Take(2).ToList();
            var pos1 = CurrentPositions[touchIds[0]];
            var pos2 = CurrentPositions[touchIds[1]];

            return Math.Sqrt(Math.Pow(pos2.X - pos1.X, 2) + Math.Pow(pos2.Y - pos1.Y, 2));
        }

        public Point CalculateCenterOfTouchPoints()
        {
            if (CurrentPositions.Count == 0) return new Point(0, 0);

            double totalX = 0, totalY = 0;
            foreach (var pos in CurrentPositions.Values)
            {
                totalX += pos.X;
                totalY += pos.Y;
            }

            return new Point(totalX / CurrentPositions.Count, totalY / CurrentPositions.Count);
        }
    }
}