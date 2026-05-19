-- AMC traffic-hour to conversion-hour lag behavior.
-- Export columns are named to match /api/amc/import-results?resultType=attribution-lag.
-- This links ad traffic to attributed conversion time so dayparting can distinguish traffic hour from purchase hour.

SELECT
  t.campaign_id,
  t.targeting AS targeting_text,
  t.customer_search_term AS search_term,
  CAST(t.event_dt AS DATE) AS traffic_date,
  EXTRACT(HOUR FROM t.event_dt) AS traffic_hour,
  CAST(c.conversion_event_dt AS DATE) AS conversion_date,
  EXTRACT(HOUR FROM c.conversion_event_dt) AS conversion_hour,
  DATE_DIFF('hour', t.event_dt, c.conversion_event_dt) AS hours_to_conversion,
  SUM(c.purchases) AS purchases,
  SUM(c.total_product_sales) AS sales
FROM sponsored_ads_traffic t
JOIN amazon_attributed_events_by_conversion_time c
  ON t.request_tag = c.traffic_event_id
WHERE t.event_dt BETWEEN @start_date AND @end_date
  AND c.conversion_event_dt BETWEEN @start_date AND @end_date
GROUP BY
  t.campaign_id,
  t.targeting,
  t.customer_search_term,
  CAST(t.event_dt AS DATE),
  EXTRACT(HOUR FROM t.event_dt),
  CAST(c.conversion_event_dt AS DATE),
  EXTRACT(HOUR FROM c.conversion_event_dt),
  DATE_DIFF('hour', t.event_dt, c.conversion_event_dt);
