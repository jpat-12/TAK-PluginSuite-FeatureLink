package com.atakmap.android.featurelink;

import android.net.Uri;
import android.os.Bundle;

import com.atakmap.android.importexport.AbstractImporter;
import com.atakmap.android.importexport.Importer;
import com.atakmap.comms.CommsMapComponent;

import java.io.IOException;
import java.io.InputStream;
import java.util.Collections;
import java.util.Set;

/**
 * Actually applies a FeatureLink layer-share config once {@link FeatureLinkMarshal} has
 * identified an incoming file as one — the receiving half of layer sharing, invoked
 * automatically by ATAK's import system when a Mission Package with
 * MissionPackageConfiguration.setImportInstructions(true, ...) is accepted (see
 * FeatureLinkDropDownReceiver.sendLayerShare()).
 */
final class FeatureLinkImporter extends AbstractImporter {

    static final Importer INSTANCE = new FeatureLinkImporter();

    /** Set once by FeatureLinkMapComponent, since that's where the DropDownReceiver holding
     * all the layer state and applyScannedPayload() actually lives. A share could in theory
     * arrive before that's wired up on a cold start — importData() just defers in that case,
     * same as ATAK's own importers do when they're not ready yet. */
    static FeatureLinkDropDownReceiver receiver;

    private FeatureLinkImporter() {
        super(FeatureLinkMarshal.CONTENT_TYPE);
    }

    @Override
    public Set<String> getSupportedMIMETypes() {
        return Collections.singleton(FeatureLinkMarshal.CONTENT_TYPE);
    }

    @Override
    public CommsMapComponent.ImportResult importData(InputStream inputStream, String mime, Bundle b)
            throws IOException {
        return CommsMapComponent.ImportResult.FAILURE; // file-based only — see FeatureLinkMarshal
    }

    @Override
    public CommsMapComponent.ImportResult importData(Uri uri, String mime, Bundle b) throws IOException {
        if (receiver == null) return CommsMapComponent.ImportResult.DEFERRED;
        return receiver.importFeatureLinkShareUri(uri)
                ? CommsMapComponent.ImportResult.SUCCESS
                : CommsMapComponent.ImportResult.FAILURE;
    }
}
