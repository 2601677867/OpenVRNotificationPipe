﻿using BOLL7708;
using System;
using System.Diagnostics;
using System.Threading;
using Valve.VR;

namespace OpenVRNotificationPipe.Notification
{
    class Animator
    {
        private readonly Texture _texture;
        private readonly ulong _overlayHandle = 0;
        private readonly EasyOpenVRSingleton _vr = EasyOpenVRSingleton.Instance;
        private Action _requestForNewPayload = null;
        private volatile Payload _payload;
        private volatile int _hz = 60;
        private volatile bool _shouldShutdown = false;

        public Animator(ulong overlayHandle, Action requestForNewAnimation)
        {
            _overlayHandle = overlayHandle;
            _requestForNewPayload = requestForNewAnimation;
            
            _texture = new Texture(_overlayHandle);
            
            var thread = new Thread(Worker);
            if (!thread.IsAlive) thread.Start();
        }

        public enum AnimationStage {
            Idle,
            EasingIn,
            Staying,
            EasingOut,
            Finished
        }

        private void Worker() {
            Thread.CurrentThread.IsBackground = true;
            
            // General
            var hmdTransform = EasyOpenVRSingleton.Utils.GetEmptyTransform();
            var notificationTransform = EasyOpenVRSingleton.Utils.GetEmptyTransform();
            var animationTransform = EasyOpenVRSingleton.Utils.GetEmptyTransform();
            var width = 1f;
            var height = 1f;
            Payload.Properties properties = null;
            Payload.Transition transition = null;

            // Animation
            var stage = AnimationStage.Idle;
            var hz = _hz; // Default used if there is none in payload
            var msPerFrame = 1000 / hz;
            long timeStarted;

            var animationCount = 0;
            var easeInCount = 0;
            var stayCount = 0;
            var easeOutCount = 0;

            var easeInLimit = 0;
            var stayLimit = 0;
            var easeOutLimit = 0;

            Func<float, float> interpolator = Interpolator.GetFunc(0);

            while (true)
            {
                // TODO: See if we can keep the overlay horizontal, if that is even necessary?!
                timeStarted = DateTime.Now.Ticks;
                
                if (_payload == null) // Get new payload
                {
                    _requestForNewPayload();
                    Thread.Sleep(100);
                }
                else if (stage == AnimationStage.Idle)
                {
                    // Initialize things that stay the same during the whole animation

                    properties = _payload.properties;
                    stage = AnimationStage.EasingIn;
                    hz = properties.hz > 0 ? properties.hz : _hz; // Update in case it has changed.
                    msPerFrame = 1000 / hz;

                    // Size of overlay
                    var size = _texture.Load(_payload.image);
                    width = properties.width;
                    height = width / size.v0 * size.v1;
                    Debug.WriteLine($"Texture width: {size.v0}, height: {size.v1}");

                    // Animation limits
                    easeInCount = _payload.transition.duration / msPerFrame;
                    stayCount = properties.duration / msPerFrame;
                    easeOutCount = (_payload.transition2?.duration ?? _payload.transition.duration) / msPerFrame;
                    easeInLimit = easeInCount;
                    stayLimit = easeInLimit + stayCount;
                    easeOutLimit = stayLimit + easeOutCount;
                    // Debug.WriteLine($"{easeInCount}, {stayCount}, {easeOutCount} - {easeInLimit}, {stayLimit}, {easeOutLimit}");

                    // Pose
                    hmdTransform = _vr.GetDeviceToAbsoluteTrackingPose()[0].mDeviceToAbsoluteTracking;
                } 
                
                if(stage != AnimationStage.Idle) // Animate
                {
                    // Animation stage
                    if (animationCount < easeInLimit) stage = AnimationStage.EasingIn;
                    else if (animationCount >= stayLimit) stage = AnimationStage.EasingOut;
                    else stage = AnimationStage.Staying;

                    // Secondary inits that happen at the start of specific stages

                    if (animationCount == 0) 
                    { // Init EaseIn
                        transition = _payload.transition;
                        interpolator = Interpolator.GetFunc(transition.interpolation);
                    }

                    if (animationCount == stayLimit)
                    { // Init EaseOut
                        if (_payload.transition2 != null)
                        {
                            transition = _payload.transition2;
                            interpolator = Interpolator.GetFunc(transition.interpolation);
                        }
                    }

                    // Setup and normalized progression ratio
                    var ratioReversed = 0f;
                    if(stage == AnimationStage.EasingIn) {
                        ratioReversed = 1f - ((float)animationCount / easeInCount);
                    } else if(stage == AnimationStage.EasingOut) {
                        ratioReversed = ((float)animationCount - stayLimit + 1) / easeOutCount; // +1 because we moved where we increment animationCount
                    }
                    ratioReversed = interpolator(ratioReversed);
                    var ratio = 1 - ratioReversed;

                    // Transform
                    if (stage != AnimationStage.Staying || animationCount == easeInLimit) { // Only performs animation on first frame of Staying stage.
                        // Debug.WriteLine($"{animationCount} - {Enum.GetName(typeof(AnimationStage), stage)} - {Math.Round(ratio*100)/100}");
                        animationTransform = (properties.headset ? EasyOpenVRSingleton.Utils.GetEmptyTransform() : hmdTransform)
                            .RotateY(-properties.yaw)
                            .RotateX(properties.pitch)
                            .Translate(new HmdVector3_t() {
                                v0 = transition.horizontal * ratioReversed,
                                v1 = transition.vertical * ratioReversed, 
                                v2 = -properties.distance - (transition.distance * ratioReversed)
                            })
                            .RotateZ(transition.spin * ratioReversed);
                        _vr.SetOverlayTransform(_overlayHandle, animationTransform, properties.headset ? 0 : uint.MaxValue);
                        _vr.SetOverlayAlpha(_overlayHandle, transition.opacity+(ratio*(1f-transition.opacity)));
                        _vr.SetOverlayWidth(_overlayHandle, width*(transition.scale+(ratio*(1f-transition.scale))));
                    }
                    // Do not make overlay visible until we have applied all the movements etc, only needs to happen the first frame.
                    if (animationCount == 0) _vr.SetOverlayVisibility(_overlayHandle, true);
                    animationCount++;

                    // We're done
                    if (animationCount >= easeOutLimit) stage = AnimationStage.Finished;

                    if (stage == AnimationStage.Finished) {
                        Debug.WriteLine("DONE!");
                        _vr.SetOverlayVisibility(_overlayHandle, false);
                        stage = AnimationStage.Idle;                        
                        properties = null;
                        animationCount = 0;
                        _payload = null;
                        _texture.Unload();
                    }
                }

                if (_shouldShutdown) { // Finish
                    _texture.Unload(); // TODO: Watch for possible instability here depending on what is going on timing-wise...
                    OpenVR.Overlay.DestroyOverlay(_overlayHandle);
                    Thread.CurrentThread.Abort();
                }

                var timeSpent = (int) Math.Round((double) (DateTime.Now.Ticks - timeStarted) / TimeSpan.TicksPerMillisecond);
                Thread.Sleep(Math.Max(1, msPerFrame-timeSpent)); // Animation time per frame adjusted by the time it took to animate.
            }

        }

        public void ProvideNewPayload(Payload payload) {
            _payload = payload;
        }

        public void SetAnimationHz(int hz) {
            _hz = hz;
        }

        public void Shutdown() {
            _requestForNewPayload = () => { };
            _payload = null;
            _shouldShutdown = true;
        }
    }
}
