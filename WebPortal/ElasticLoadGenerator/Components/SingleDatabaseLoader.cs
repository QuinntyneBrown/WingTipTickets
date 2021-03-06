﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data.SqlClient;
using System.Threading;
using ElasticPoolLoadGenerator.Helpers;
using ElasticPoolLoadGenerator.Interfaces;
using ElasticPoolLoadGenerator.Models;
using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;

namespace ElasticPoolLoadGenerator.Components
{
    public class SingleDatabaseLoader : IDatabaseLoader
    {
        #region - Fields -

        private double _totalElapsedSeconds;

        private readonly BackgroundWorker _worker;
        private readonly MainViewModel _model;
        private readonly DateTime _startTime;
        private ExponentialBackoff _backoffStrategy;

        #endregion

        #region - Constructors -

        public SingleDatabaseLoader(MainViewModel viewModel)
        {
            _model = viewModel;
            _startTime = DateTime.Now;

            // Setup the Background Worker
            _worker = new BackgroundWorker
            {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = true
            };

            _worker.DoWork += Worker_DoWork;
            _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
            _worker.ProgressChanged += Worker_ProgressChanged;

            // Initialize Backoff Strategy
            InitializeBackoffStrategy();
        }

        #endregion

        #region - Public Methods -

        public void Start()
        {
            _worker.RunWorkerAsync();
        }

        public void Stop()
        {
            _worker.CancelAsync();
        }

        #endregion

        #region - Private Methods -

        private void InitializeBackoffStrategy()
        {
            try
            {
                // Create Backoff Strategy
                _backoffStrategy = new ExponentialBackoff(
                    "exponentialBackoffStrategy",
                    Convert.ToInt32(ConfigurationManager.AppSettings["TransientFaultHandlingRetryCount"].Trim()),
                    TimeSpan.FromSeconds(Convert.ToInt32(ConfigurationManager.AppSettings["TransientFaultHandlingMinBackoffDelaySeconds"].Trim())),
                    TimeSpan.FromSeconds(Convert.ToInt32(ConfigurationManager.AppSettings["TransientFaultHandlingMaxBackoffDelaySeconds"].Trim())),
                    TimeSpan.FromSeconds(Convert.ToInt32(ConfigurationManager.AppSettings["TransientFaultHandlingDeltaBackoffSeconds"].Trim())));

                // Set default retry manager
                RetryManager.SetDefault(new RetryManager(new List<RetryStrategy>
                {
                    _backoffStrategy
                }, "exponentialBackoffStrategy"));
            }
            catch
            {
            }
        }

        private void LoadDatabase(RetryPolicy<ErrorDetectionStrategy> retryPolicy, string batchQuery)
        {
            // Reset load Values
            var ticketsPurchased = 0d;
            var loadStartTime = DateTime.Now;

            // Build the Connection String
            var connectionString = DatabaseHelper.ConstructConnectionString(_model.DatabaseServer, _model.PrimaryDatabase, _model.Username, _model.Password);

            using (var sqlConnection = new ReliableSqlConnection(connectionString, retryPolicy))
            {
                sqlConnection.Open(retryPolicy);

                using (var cmd = new SqlCommand(batchQuery, sqlConnection.Current))
                {
                    do
                    {
                        if (_worker.CancellationPending)
                        {
                            break;
                        }

                        // Run the batch insert script
                        cmd.Transaction = (SqlTransaction)sqlConnection.BeginTransaction(); ;
                        cmd.ExecuteNonQueryWithRetry(retryPolicy);
                        cmd.Transaction.Commit();

                        // Update Values
                        ticketsPurchased += _model.BulkPurchaseQty;

                        UpdateTotalValues();
                        ReportProgress(ticketsPurchased, loadStartTime, _model.PrimaryDatabase);

                        // Minimize tickets added
                        Thread.Sleep(5);
                    }
                    while (_totalElapsedSeconds < 6 * 60 * 60); // Quit this after 6 hours
                }
            }
        }

        private void ReportProgress(double ticketsPurchased, DateTime loadStartTime, string database = "")
        {
            // Calculate values
            var loadElapsedSeconds = (DateTime.Now - loadStartTime).TotalSeconds;

            // Build value object
            var values = new ProgressValues()
            {
                ElapsedMinutes = Convert.ToInt32(_totalElapsedSeconds / 60),
                TotalMinutes = Convert.ToInt32(ConfigHelper.Runtime / 60),
                PurchasesPerSecond = Math.Round(ticketsPurchased / loadElapsedSeconds, 2),
                LoadingDatabase = string.Format("Loading: {0}", database)
            };

            // Check for NaN
            values.PurchasesPerSecond = !double.IsNaN(values.PurchasesPerSecond) ? values.PurchasesPerSecond : 0d;

            // Report on Progress
            _worker.ReportProgress(0, values);
        }

        private void UpdateTotalValues()
        {
            // Update totals
            _totalElapsedSeconds = (DateTime.Now - _startTime).TotalSeconds;
        }

        #endregion

        #region - Event Methods -

        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                // Build the Queries
                var rootQuery = DatabaseHelper.BuildInsertQuery();
                var batchQuery = DatabaseHelper.BuildBatchQuery(_model.BulkPurchaseQty, rootQuery);

                // Create the Retry Policy
                var retryPolicy = new RetryPolicy<ErrorDetectionStrategy>(_backoffStrategy);

                retryPolicy.ExecuteAction(() => LoadDatabase(retryPolicy, batchQuery));

                if (_worker.CancellationPending)
                {
                    e.Cancel = true;
                }
            }
            catch (Exception ex)
            {
                e.Result = ex.Message;
            }
        }

        void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Get the progress values
            var values = (ProgressValues)e.UserState;

            // Update the model
            _model.ProgressValue = e.ProgressPercentage;
            _model.StatusText = "Loading until manually stopped";
            _model.PurchasesPerSecond = values.PurchasesPerSecond;
            _model.LoadingDatabase = values.LoadingDatabase;
        }

        void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Reset the model values
            _model.PurchasesPerSecond = 0;
            _model.ProgressValue = 0;
            _model.FieldsEnabled = true;
            _model.StartText = "Start";
            _model.StatusText = "";

            _model.LoadingDatabase = "Load Generation " + (e.Cancelled ? "Cancelled" : "Completed");
        }

        #endregion

        #region - ProgressValues class -

        private class ProgressValues
        {
            public double PurchasesPerSecond { get; set; }
            public int ElapsedMinutes { get; set; }
            public int TotalMinutes { get; set; }
            public string LoadingDatabase { get; set; }
        }

        #endregion
    }
}
