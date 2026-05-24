-- AMC Sponsored Ads traffic by traffic hour.
-- Verified by AMC Agent on amcjk0ydh5o.
-- Notes:
--   - Date range is set by the workflow execution's timeWindowStart/End (no WHERE filter needed).
--   - event_hour is a direct integer column (advertiser timezone); use it instead of EXTRACT.
--   - spend is in micro-microcents; divide by 1e8 to get local currency.
--   - time_zone literal removed: event timestamps here are advertiser TZ, labeling them 'UTC' was misleading.

SELECT
  CAST(event_dt AS DATE) AS traffic_date,
  event_hour AS traffic_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  targeting AS targeting_text,
  match_type,
  customer_search_term,
  SUM(impressions) AS impressions,
  SUM(clicks) AS clicks,
  SUM(spend) / 100000000.0 AS spend
FROM sponsored_ads_traffic
GROUP BY
  CAST(event_dt AS DATE),
  event_hour,
  campaign_id_string,
  campaign,
  ad_product_type,
  targeting,
  match_type,
  customer_search_term;
