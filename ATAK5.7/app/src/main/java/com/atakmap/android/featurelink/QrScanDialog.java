package com.atakmap.android.featurelink;

import android.app.Dialog;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.SurfaceTexture;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.Image;
import android.media.ImageReader;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;

import android.util.Log;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.NotFoundException;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;

import java.nio.ByteBuffer;
import java.util.Arrays;

/**
 * Full-screen camera dialog for scanning QR codes.
 *
 * Uses Dialog instead of Activity because ATAK plugin Activities are not registered
 * with the Android ActivityManager (the plugin APK is loaded in-process via
 * DexClassLoader, not installed as a standalone APK). A Dialog bypasses
 * ActivityManager entirely and runs fine inside the plugin process.
 *
 * Must be constructed with the ATAKActivity context (getMapView().getContext()),
 * NOT pluginContext — same rule as all other dialogs in the plugin.
 */
public class QrScanDialog extends Dialog implements TextureView.SurfaceTextureListener {

    private static final String TAG = "QrScanDialog";

    public interface Callback {
        void onScanned(String payload);
    }

    private final Callback callback;

    private TextureView textureView;

    private CameraManager cameraManager;
    private String cameraId;
    private CameraDevice cameraDevice;
    private CameraCaptureSession captureSession;
    private ImageReader imageReader;

    private HandlerThread backgroundThread;
    private Handler backgroundHandler;

    private final QRCodeReader qrReader = new QRCodeReader();
    private volatile boolean decoding = true;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    public QrScanDialog(Context atakContext, Callback callback) {
        super(atakContext, android.R.style.Theme_Black_NoTitleBar_Fullscreen);
        this.callback = callback;
        setCanceledOnTouchOutside(false);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Window window = getWindow();
        if (window != null) {
            window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN
                    | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
        buildLayout();
    }

    @Override
    public void onStart() {
        super.onStart();
        Window window = getWindow();
        if (window != null) {
            window.setLayout(ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT);
        }
        decoding = true;
        if (textureView.isAvailable()) {
            startBackgroundThread();
            openCamera(textureView.getWidth(), textureView.getHeight());
        } else {
            textureView.setSurfaceTextureListener(this);
        }
    }

    @Override
    public void onStop() {
        super.onStop();
        closeCamera();
        stopBackgroundThread();
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------

    private static final int FRAME_DP = 260; // square viewfinder size

    private void buildLayout() {
        FrameLayout root = new FrameLayout(getContext());
        root.setBackgroundColor(0xFF000000);

        // Full-screen camera preview behind everything
        textureView = new TextureView(getContext());
        root.addView(textureView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));

        // Dim overlay with square cutout + corner brackets
        root.addView(new ViewfinderView(getContext(), dp(FRAME_DP)),
                new FrameLayout.LayoutParams(
                        FrameLayout.LayoutParams.MATCH_PARENT,
                        FrameLayout.LayoutParams.MATCH_PARENT));

        // Label + cancel button anchored to the bottom
        android.widget.LinearLayout bottom = new android.widget.LinearLayout(getContext());
        bottom.setOrientation(android.widget.LinearLayout.VERTICAL);
        bottom.setGravity(Gravity.CENTER_HORIZONTAL);

        TextView statusText = new TextView(getContext());
        statusText.setText("Point camera at QR code");
        statusText.setTextColor(0xFFFFFFFF);
        statusText.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        statusText.setGravity(Gravity.CENTER);
        android.widget.LinearLayout.LayoutParams tvLp =
                new android.widget.LinearLayout.LayoutParams(
                        android.widget.LinearLayout.LayoutParams.WRAP_CONTENT,
                        android.widget.LinearLayout.LayoutParams.WRAP_CONTENT);
        tvLp.bottomMargin = dp(8);
        bottom.addView(statusText, tvLp);

        Button cancelBtn = new Button(getContext());
        cancelBtn.setText("Cancel");
        cancelBtn.setTextColor(0xFFFFFFFF);
        cancelBtn.setBackgroundColor(0x88333333);
        cancelBtn.setOnClickListener(v -> dismiss());
        android.widget.LinearLayout.LayoutParams btnLp =
                new android.widget.LinearLayout.LayoutParams(dp(120), dp(44));
        bottom.addView(cancelBtn, btnLp);

        FrameLayout.LayoutParams bottomLp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT);
        bottomLp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        bottomLp.bottomMargin = dp(20);
        root.addView(bottom, bottomLp);

        setContentView(root);
    }

    // -------------------------------------------------------------------------
    // Square viewfinder overlay
    // -------------------------------------------------------------------------

    private static final class ViewfinderView extends android.view.View {
        private final Paint dimPaint    = new Paint();
        private final Paint cornerPaint = new Paint();
        private final RectF frame       = new RectF();
        private final int frameSize;

        ViewfinderView(Context ctx, int framePx) {
            super(ctx);
            this.frameSize = framePx;
            dimPaint.setColor(0xBB000000);
            cornerPaint.setColor(0xFFFFFFFF);
            cornerPaint.setStyle(Paint.Style.STROKE);
            cornerPaint.setStrokeWidth(6f);
            cornerPaint.setStrokeCap(Paint.Cap.SQUARE);
        }

        @Override
        protected void onSizeChanged(int w, int h, int ow, int oh) {
            // Centre the square; sit it slightly above centre so bottom controls fit
            float left = (w - frameSize) / 2f;
            float top  = (h - frameSize) / 2f - h * 0.05f;
            frame.set(left, top, left + frameSize, top + frameSize);
        }

        @Override
        protected void onDraw(Canvas canvas) {
            // Four dark panels around the clear square
            canvas.drawRect(0,            0,             getWidth(),   frame.top,    dimPaint);
            canvas.drawRect(0,            frame.bottom,  getWidth(),   getHeight(),  dimPaint);
            canvas.drawRect(0,            frame.top,     frame.left,   frame.bottom, dimPaint);
            canvas.drawRect(frame.right,  frame.top,     getWidth(),   frame.bottom, dimPaint);

            // Corner brackets — length is 18% of the frame side
            float arm = frameSize * 0.18f;
            float l = frame.left,  t = frame.top;
            float r = frame.right, b = frame.bottom;

            // Top-left
            canvas.drawLine(l, t, l + arm, t, cornerPaint);
            canvas.drawLine(l, t, l, t + arm, cornerPaint);
            // Top-right
            canvas.drawLine(r - arm, t, r, t, cornerPaint);
            canvas.drawLine(r, t, r, t + arm, cornerPaint);
            // Bottom-left
            canvas.drawLine(l, b - arm, l, b, cornerPaint);
            canvas.drawLine(l, b, l + arm, b, cornerPaint);
            // Bottom-right
            canvas.drawLine(r, b - arm, r, b, cornerPaint);
            canvas.drawLine(r - arm, b, r, b, cornerPaint);
        }
    }

    // -------------------------------------------------------------------------
    // TextureView.SurfaceTextureListener
    // -------------------------------------------------------------------------

    @Override
    public void onSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) {
        startBackgroundThread();
        openCamera(width, height);
    }

    @Override public void onSurfaceTextureSizeChanged(SurfaceTexture s, int w, int h) {}
    @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture s) { return true; }
    @Override public void onSurfaceTextureUpdated(SurfaceTexture s) {}

    // -------------------------------------------------------------------------
    // Camera2
    // -------------------------------------------------------------------------

    private void openCamera(int previewWidth, int previewHeight) {
        cameraManager = (CameraManager) getContext().getSystemService(Context.CAMERA_SERVICE);
        try {
            cameraId = findBackCamera();
            if (cameraId == null) {
                Log.e(TAG, "No camera found");
                mainHandler.post(this::dismiss);
                return;
            }

            imageReader = ImageReader.newInstance(
                    1280, 720, android.graphics.ImageFormat.YUV_420_888, 2);
            imageReader.setOnImageAvailableListener(this::onImageAvailable, backgroundHandler);

            cameraManager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice camera) {
                    cameraDevice = camera;
                    createCaptureSession(previewWidth, previewHeight);
                }

                @Override
                public void onDisconnected(CameraDevice camera) {
                    camera.close();
                    cameraDevice = null;
                }

                @Override
                public void onError(CameraDevice camera, int error) {
                    Log.e(TAG, "Camera error " + error);
                    camera.close();
                    cameraDevice = null;
                    mainHandler.post(QrScanDialog.this::dismiss);
                }
            }, backgroundHandler);

        } catch (CameraAccessException | SecurityException e) {
            Log.e(TAG, "openCamera failed", e);
            mainHandler.post(this::dismiss);
        }
    }

    private String findBackCamera() throws CameraAccessException {
        String fallback = null;
        for (String id : cameraManager.getCameraIdList()) {
            CameraCharacteristics c = cameraManager.getCameraCharacteristics(id);
            Integer facing = c.get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) return id;
            if (fallback == null) fallback = id;
        }
        return fallback;
    }

    // Buffer dimensions the camera will deliver (always landscape from sensor).
    private static final int BUF_W = 1280;
    private static final int BUF_H = 720;

    /**
     * Corrects the TextureView transform so the camera buffer (BUF_W × BUF_H,
     * in the sensor's natural orientation) appears upright and fills the view.
     * Based on Google's Camera2Basic configureTransform pattern.
     */
    private void configureTransform(int viewWidth, int viewHeight) {
        if (cameraId == null) return;
        int sensorOrientation = 0;
        try {
            CameraCharacteristics c = cameraManager.getCameraCharacteristics(cameraId);
            Integer o = c.get(CameraCharacteristics.SENSOR_ORIENTATION);
            if (o != null) sensorOrientation = o;
        } catch (CameraAccessException e) {
            return;
        }

        int displayRotation;
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R) {
            android.view.Display disp = getContext().getDisplay();
            displayRotation = disp != null ? disp.getRotation() : Surface.ROTATION_0;
        } else {
            displayRotation = ((WindowManager) getContext()
                    .getSystemService(Context.WINDOW_SERVICE))
                    .getDefaultDisplay().getRotation();
        }

        Matrix matrix = new Matrix();
        RectF viewRect   = new RectF(0, 0, viewWidth, viewHeight);
        RectF bufferRect = new RectF(0, 0, BUF_H, BUF_W); // swapped — sensor buffer in portrait coords
        float cx = viewRect.centerX();
        float cy = viewRect.centerY();

        if (Surface.ROTATION_90 == displayRotation || Surface.ROTATION_270 == displayRotation) {
            bufferRect.offset(cx - bufferRect.centerX(), cy - bufferRect.centerY());
            matrix.setRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.FILL);
            float scale = Math.max(
                    (float) viewHeight / BUF_H,
                    (float) viewWidth  / BUF_W);
            matrix.postScale(scale, scale, cx, cy);
            matrix.postRotate(90 * (displayRotation - 2), cx, cy);
        } else if (Surface.ROTATION_180 == displayRotation) {
            matrix.postRotate(180, cx, cy);
        }
        // ROTATION_0 (portrait) needs no additional correction beyond the rect-to-rect above

        textureView.setTransform(matrix);
    }

    private void createCaptureSession(int viewWidth, int viewHeight) {
        try {
            SurfaceTexture st = textureView.getSurfaceTexture();
            // Always set buffer to the fixed camera output size, not the view size.
            st.setDefaultBufferSize(BUF_W, BUF_H);
            configureTransform(viewWidth, viewHeight);
            Surface previewSurface = new Surface(st);
            Surface readerSurface  = imageReader.getSurface();

            cameraDevice.createCaptureSession(
                    Arrays.asList(previewSurface, readerSurface),
                    new CameraCaptureSession.StateCallback() {
                        @Override
                        public void onConfigured(CameraCaptureSession session) {
                            if (cameraDevice == null) return;
                            captureSession = session;
                            try {
                                CaptureRequest.Builder req = cameraDevice.createCaptureRequest(
                                        CameraDevice.TEMPLATE_PREVIEW);
                                req.addTarget(previewSurface);
                                req.addTarget(readerSurface);
                                req.set(CaptureRequest.CONTROL_AF_MODE,
                                        CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                                captureSession.setRepeatingRequest(
                                        req.build(), null, backgroundHandler);
                            } catch (CameraAccessException e) {
                                Log.e(TAG, "Repeating request failed", e);
                            }
                        }

                        @Override
                        public void onConfigureFailed(CameraCaptureSession session) {
                            Log.e(TAG, "Capture session configure failed");
                            mainHandler.post(QrScanDialog.this::dismiss);
                        }
                    }, backgroundHandler);

        } catch (CameraAccessException e) {
            Log.e(TAG, "createCaptureSession failed", e);
        }
    }

    // -------------------------------------------------------------------------
    // ZXing frame decode
    // -------------------------------------------------------------------------

    private void onImageAvailable(ImageReader reader) {
        if (!decoding) return;
        Image image = reader.acquireLatestImage();
        if (image == null) return;
        try {
            Image.Plane[] planes = image.getPlanes();
            ByteBuffer yBuf = planes[0].getBuffer();
            byte[] yData = new byte[yBuf.remaining()];
            yBuf.get(yData);

            int w = image.getWidth();
            int h = image.getHeight();

            PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                    yData, w, h, 0, 0, w, h, false);
            BinaryBitmap bmp = new BinaryBitmap(new HybridBinarizer(source));
            Result result = qrReader.decode(bmp);

            decoding = false;
            String text = result.getText();
            mainHandler.post(() -> {
                if (callback != null) callback.onScanned(text);
                dismiss();
            });

        } catch (NotFoundException ignored) {
        } catch (Exception e) {
            Log.e(TAG, "Decode error", e);
        } finally {
            image.close();
        }
    }

    // -------------------------------------------------------------------------
    // Camera lifecycle helpers
    // -------------------------------------------------------------------------

    private void closeCamera() {
        try {
            if (captureSession != null) { captureSession.close(); captureSession = null; }
            if (cameraDevice  != null) { cameraDevice.close();   cameraDevice  = null; }
            if (imageReader   != null) { imageReader.close();    imageReader   = null; }
        } catch (Exception e) {
            Log.e(TAG, "closeCamera error", e);
        }
    }

    private void startBackgroundThread() {
        backgroundThread = new HandlerThread("QrCameraBackground");
        backgroundThread.start();
        backgroundHandler = new Handler(backgroundThread.getLooper());
    }

    private void stopBackgroundThread() {
        if (backgroundThread != null) {
            backgroundThread.quitSafely();
            try { backgroundThread.join(500); } catch (InterruptedException ignored) {}
            backgroundThread = null;
            backgroundHandler = null;
        }
    }

    private int dp(int value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getContext().getResources().getDisplayMetrics()));
    }
}
