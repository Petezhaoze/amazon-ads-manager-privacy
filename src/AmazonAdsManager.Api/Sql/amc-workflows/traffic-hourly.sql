-- AMC Sponsored Ads traffic by traffic hour.
-- Export columns are named to match /api/amc/import-results?resultType=traffic-hourly.
-- Adjust table names if your AMC instance uses different canonical views.

SELECT
  CAST(event_dt AS DATE) AS traffic_date,
  EXTRACT(HOUR FROM event_dt) AS traffic_hour,
  'UTC' AS time_zone,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  targeting AS targeting_text,
  match_type,
  customer_search_term,
  SUM(impressions) AS impressions,
  SUM(clicks) AS clicks,
  SUM(spend) AS spend
FROM sponsored_ads_traffic
WHERE event_dt BETWEEN @start_date AND @end_date
GROUP BY
  CAST(event_dt AS DATE),
  EXTRACT(HOUR FROM event_dt),
  campaign_id_string,
  campaign,
  ad_product_type,
  targeting,
  match_type,
  customer_search_term;
