package com.atakmap.android.featurelink.plugin;

import android.content.Context;
import com.atak.plugins.impl.AbstractPluginTool;

public class FeatureLinkTool extends AbstractPluginTool {

    public FeatureLinkTool(Context context) {
        super(context,
                context.getString(R.string.app_name),
                context.getString(R.string.app_name),
                context.getResources().getDrawable(R.drawable.ic_launcher),
                "com.atakmap.android.featurelink.SHOW_PLUGIN");
    }
}
