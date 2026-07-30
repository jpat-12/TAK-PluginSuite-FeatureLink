package com.atakmap.android.featurelink.plugin;

import com.atak.plugins.impl.AbstractPlugin;
import com.atak.plugins.impl.PluginContextProvider;
import com.atakmap.android.featurelink.FeatureLinkMapComponent;
import gov.tak.api.plugin.IServiceController;

public class FeatureLinkLifecycle extends AbstractPlugin {

    public FeatureLinkLifecycle(IServiceController serviceController) {
        super(serviceController,
                new FeatureLinkTool(serviceController
                        .getService(PluginContextProvider.class)
                        .getPluginContext()),
                new FeatureLinkMapComponent());
    }
}
