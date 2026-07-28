package com.atakmap.android.featurelink;

import android.net.Uri;

import com.atakmap.android.importexport.AbstractMarshal;
import com.atakmap.android.importexport.Marshal;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.util.Locale;

/**
 * Identifies an incoming file as a FeatureLink layer-share config (see
 * LayerShareHelper.buildShareConfigJson()) by its ".featurelink.json" naming convention, so an
 * ATAK Mission Package containing one — accepted via ATAK's own native "X wants to send you a
 * file" prompt — gets automatically routed to FeatureLinkImporter instead of just sitting on
 * disk waiting for a manual "Upload Pref File" pick. Pattern matches the SDK's own
 * importexportexample sample (ExFmtMarshal).
 */
final class FeatureLinkMarshal extends AbstractMarshal {

    static final String CONTENT_TYPE = "FeatureLink Layer Config";
    static final Marshal INSTANCE = new FeatureLinkMarshal();

    private FeatureLinkMarshal() {
        super(CONTENT_TYPE);
    }

    @Override
    public String marshal(InputStream inputStream, int limit) throws IOException {
        // File-based only, matching the SDK sample — Mission Package extraction always hands
        // us a real file on disk, never a bare stream.
        return null;
    }

    @Override
    public String marshal(Uri uri) throws IOException {
        String path = uri.getPath();
        if (path == null || !path.toLowerCase(Locale.US).endsWith(".featurelink.json")) return null;
        File f = new File(path);
        return f.exists() ? CONTENT_TYPE : null;
    }

    @Override
    public int getPriorityLevel() {
        return 1;
    }
}
