-- AMC Sponsored Ads traffic by traffic hour.
-- Export columns are named to match /api/amc/import-results?resultType=traffic-hourly.
-- Adjust table names if your AMC instance uses different canonical views.

SELECT
  CAST(event_dt AS DATE) AS date,
  EXTRACT(HOUR FROM event_dt) AS hour,
  'UTC' AS time_zone,
  advertiser_id AS profile_id,
  campaign_id,
  campaign AS campaign_name,
  ad_group_id,
  ad_group AS ad_group_name,
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
  advertiser_id,
  campaign_id,
  campaign,
  ad_group_id,
  ad_group,
  ad_product_type,
  targeting,
  match_type,
  customer_search_term;
