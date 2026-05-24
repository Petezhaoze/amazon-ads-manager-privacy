-- AMC Sponsored Ads traffic aggregated to (date, hour, campaign).
-- Empirical verification on amcjk0ydh5o, 2026-05-24:
--   At hourly grain GROUPed by (date, hour, campaign, targeting, match_type, customer_search_term),
--   AMC's privacy aggregation suppressed the `traffic_date` column to "" on 100% of returned rows
--   (4,214 rows, 0% with populated date). The cartesian product had too few users per cell. We
--   tried the AMC Agent's fixes and the suppression persisted -- trailing semicolons and
--   campaign were not the cause. The table schema classifies campaign as LOW threshold, so it is
--   safe at this grain. Reducing the GROUP BY to
--   (date, hour, campaign_id, campaign_name, ad_product_type) gives each cell enough users to
--   survive aggregation.
-- Trade-off: search-term / match-type / targeting granularity is dropped from AMC. That data is
-- already available from the Amazon Ads Reporting API (dbo.AdPerformanceDaily). The AMC workflow
-- only feeds HourlyScorecard, which aggregates to (Date, Hour) anyway.
-- Other notes:
--   - No trailing semicolon (CleanWorkflowSql strips it as a safety net regardless).
--   - event_date and event_hour are both advertiser-timezone columns. timeWindowTimeZone controls
--     request boundaries only; it does not shift these output fields.
--   - Use the dedicated event_date column instead of CAST(event_dt AS DATE); the timestamp cast
--     can inherit stricter aggregation thresholds and blank the date output.
--   - spend is in micro-microcents; divide by 1e8 to get local currency.

SELECT
  event_date AS traffic_date,
  event_hour AS traffic_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  SUM(impressions) AS impressions,
  SUM(clicks) AS clicks,
  SUM(spend) / 100000000.0 AS spend
FROM sponsored_ads_traffic
GROUP BY
  event_date,
  event_hour,
  campaign_id_string,
  campaign,
  ad_product_type
